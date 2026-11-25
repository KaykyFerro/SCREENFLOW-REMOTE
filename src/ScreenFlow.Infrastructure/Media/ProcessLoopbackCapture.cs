using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>
/// Som do PC sem o próprio ScreenFlow: o loopback de processo do Windows no modo "tudo menos esta árvore de processos".
/// Assim a voz da rede, os avisos e o som de outra transmissão que o app toca não voltam para quem assiste (quem está na
/// voz e assistindo escutaria a própria voz atrasada pela transmissão). Precisa do Windows 10 2004 (build 19041) ou mais
/// novo; quem usa cai no loopback normal se a ativação falhar.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private const ushort VtBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ModeIncludeTree = 0;
    private const int ModeExcludeTree = 1;
    private const long BufferDuration = 5_000_000; // 500 ms em unidades de 100 ns: só capacidade, não atrasa nada

    private readonly AudioClient _client;
    private readonly AudioCaptureClient _captureClient;
    private readonly AutoResetEvent _ready = new(false);
    private readonly int _blockAlign;
    private byte[] _buffer = new byte[48_000 * 8 / 10];
    private Thread? _thread;
    private volatile bool _stop;
    private bool _disposed;

    private ProcessLoopbackCapture(AudioClient client, WaveFormat format)
    {
        _client = client;
        WaveFormat = format;
        _blockAlign = format.BlockAlign;
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        _captureClient = _client.AudioCaptureClient;
    }

    /// <summary>O Windows tem o loopback de processo (Windows 10 2004 ou mais novo).</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Formato pedido ao Windows (ele converte sozinho): 48 kHz estéreo float, o mesmo do Opus.</summary>
    public static WaveFormat CaptureFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    /// <summary>Tudo que toca no PC menos <paramref name="processId"/> e os processos filhos dele.</summary>
    public static ProcessLoopbackCapture ExcludingProcess(int processId) => Open(processId, ModeExcludeTree);

    /// <summary>Só o que <paramref name="processId"/> e os processos filhos dele tocam.</summary>
    public static ProcessLoopbackCapture OnlyProcess(int processId) => Open(processId, ModeIncludeTree);

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private static ProcessLoopbackCapture Open(int processId, int mode)
    {
        if (!Supported) throw new PlatformNotSupportedException("O som do PC sem o ScreenFlow precisa do Windows 10 2004 ou mais novo.");
        var parameters = new ActivationParams { ActivationType = ActivationTypeProcessLoopback, TargetProcessId = (uint)processId, LoopbackMode = mode };
        var size = Marshal.SizeOf<ActivationParams>();
        var parametersPointer = Marshal.AllocHGlobal(size);
        var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        AudioClient? client = null;
        try
        {
            Marshal.StructureToPtr(parameters, parametersPointer, false);
            Marshal.StructureToPtr(new BlobVariant { Type = VtBlob, Size = (uint)size, Data = parametersPointer }, variantPointer, false);
            var handler = new AudioActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VirtualDevice, ref iid, variantPointer, handler, out var operation));
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("O Windows não respondeu ao pedido do som do PC.");
            GC.KeepAlive(operation);
            Marshal.ThrowExceptionForHR(handler.Result);
            if (handler.Client is not IAudioClient audioClient) throw new InvalidOperationException("O Windows não devolveu o cliente de áudio.");

            client = new AudioClient(audioClient);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                BufferDuration, 0, CaptureFormat, Guid.Empty);
            var capture = new ProcessLoopbackCapture(client, CaptureFormat);
            client = null;
            return capture;
        }
        finally
        {
            client?.Dispose();
            Marshal.FreeHGlobal(variantPointer);
            Marshal.FreeHGlobal(parametersPointer);
        }
    }

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenFlow som do PC", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Para sem esperar a thread (quem chama pode estar segurando uma trava que o DataAvailable usa).</summary>
    public void StopRecording()
    {
        _stop = true;
        try { _ready.Set(); } catch (ObjectDisposedException) { }
    }

    private void Run()
    {
        Exception? error = null;
        try
        {
            _client.Start();
            while (!_stop)
            {
                // O evento do WASAPI avisa quando chega áudio; o tempo limite cobre o caso de ele não vir.
                _ready.WaitOne(20);
                if (_stop) break;
                Drain();
            }
        }
        catch (Exception exception) { error = exception; }
        finally
        {
            try { _client.Stop(); } catch { }
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(_stop ? null : error));
    }

    private void Drain()
    {
        var bytes = 0;
        var frames = _captureClient.GetNextPacketSize();
        while (frames > 0 && !_stop)
        {
            var data = _captureClient.GetBuffer(out var read, out var flags);
            var length = read * _blockAlign;
            if (bytes + length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, bytes + length));
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buffer, bytes, length);
            else if (length > 0) Marshal.Copy(data, _buffer, bytes, length);
            _captureClient.ReleaseBuffer(read);
            bytes += length;
            frames = _captureClient.GetNextPacketSize();
        }
        if (bytes > 0 && !_stop) DataAvailable?.Invoke(this, new WaveInEventArgs(_buffer, bytes));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        // Espera um pouco a thread sair; se ela estiver presa no DataAvailable de quem chamou, segue mesmo assim
        // (as chamadas seguintes no cliente liberado só lançam exceção, que a thread já trata).
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(1000);
        try { _captureClient.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    /// <summary>PROPVARIANT com VT_BLOB (24 bytes em 64 bits: tipo, 3 reservados, tamanho e ponteiro alinhado).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint Size;
        public nint Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);
}

/// <summary>Recebe o resultado da ativação. É ágil (IAgileObject): o Windows chama de qualquer thread.</summary>
[ComVisible(true)]
internal sealed class AudioActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public ManualResetEventSlim Done { get; } = new();
    public int Result { get; private set; } = unchecked((int)0x80004005); // E_FAIL
    public object? Client { get; private set; }

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out var result, out var client);
            Result = result;
            Client = client;
        }
        catch (Exception exception) { Result = exception.HResult; }
        finally { Done.Set(); }
    }
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}
