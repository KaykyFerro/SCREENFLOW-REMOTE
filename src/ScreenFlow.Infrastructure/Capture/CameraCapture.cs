using System.Runtime.InteropServices;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace ScreenFlow.Infrastructure.Capture;

public sealed record CameraDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class CameraDiscovery
{
    /// <summary>Webcams e placas de captura ligadas e habilitadas no Windows.</summary>
    public static async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.Where(d => d.IsEnabled).Select(d => new CameraDevice(d.Id, d.Name)).OrderBy(d => d.Name).ToArray();
        }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível listar as câmeras: {exception.Message}");
            return [];
        }
    }
}

/// <summary>
/// Câmera pelo Windows.Media.Capture (MediaFrameReader). O Windows já entrega cada frame em BGRA (converte MJPG/NV12/YUY2);
/// o frame vai direto para a textura de trabalho na GPU e segue pelo mesmo caminho da tela (conversão para NV12 e encoder).
/// A câmera só fica ligada enquanto alguém assiste.
/// </summary>
public sealed unsafe class CameraCapture : ICaptureSource
{
    private readonly GpuDevice _gpu;
    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly object _gate = new();
    private long _lastTimestampUs;
    private volatile bool _disposed;

    public CameraCapture(GpuDevice gpu, string deviceId, int width, int height, int fps)
    {
        _gpu = gpu;
        _capture = new MediaCapture();
        var exclusive = Initialize(deviceId, MediaCaptureSharingMode.ExclusiveControl);
        if (!exclusive)
        {
            // Outro app está com a câmera: tenta só ler, no formato que ela já estiver usando.
            _capture.Dispose();
            _capture = new MediaCapture();
            if (!Initialize(deviceId, MediaCaptureSharingMode.SharedReadOnly))
                throw new InvalidOperationException("A câmera está em uso por outro aplicativo. Feche o outro app e tente de novo.");
        }

        var source = _capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderBy(s => s.Info.MediaStreamType switch { MediaStreamType.VideoRecord => 0, MediaStreamType.VideoPreview => 1, _ => 2 })
            .FirstOrDefault() ?? throw new InvalidOperationException("Esta câmera não oferece vídeo colorido.");

        if (exclusive && ChooseFormat(source.SupportedFormats, width, height, fps) is { } format)
        {
            try { source.SetFormatAsync(format).AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { Log.Warn($"A câmera recusou {Describe(format)}: {exception.Message}"); }
        }

        var current = source.CurrentFormat;
        Width = Math.Max(2, (int)current.VideoFormat.Width & ~1);
        Height = Math.Max(2, (int)current.VideoFormat.Height & ~1);
        Texture = gpu.CreateTexture(Width, Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
        Backend = $"Câmera · {Describe(current)}";

        _reader = _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().GetAwaiter().GetResult();
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        var status = _reader.StartAsync().AsTask().GetAwaiter().GetResult();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            Dispose();
            throw new InvalidOperationException(status switch
            {
                MediaFrameReaderStartStatus.DeviceNotAvailable => "A câmera não está disponível (desconectada ou em uso).",
                MediaFrameReaderStartStatus.ExclusiveControlNotAvailable => "A câmera está em uso por outro aplicativo.",
                _ => $"A câmera não iniciou ({status}).",
            });
        }
        Log.Info($"Câmera iniciada: {Backend}");
    }

    public string Backend { get; }
    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Texture { get; }
    public bool SizeChanged { get; private set; }

    private bool Initialize(string deviceId, MediaCaptureSharingMode sharing)
    {
        var settings = new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = sharing,
        };
        try
        {
            _capture.InitializeAsync(settings).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("O Windows bloqueou a câmera. Libere em Configurações › Privacidade e segurança › Câmera e ligue “Permitir que aplicativos da área de trabalho acessem a câmera”.");
        }
        catch (Exception exception) when (sharing == MediaCaptureSharingMode.ExclusiveControl)
        {
            Log.Warn($"Câmera em modo exclusivo falhou ({exception.Message}). Tentando compartilhado.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Warn($"Câmera falhou: {exception.Message}");
            return false;
        }
    }

    /// <summary>O formato mais perto do pedido: tamanho, depois FPS, preferindo os sem compressão (menos CPU).</summary>
    internal static MediaFrameFormat? ChooseFormat(IReadOnlyList<MediaFrameFormat> formats, int width, int height, int fps)
    {
        var wanted = (long)width * height;
        return formats
            .Where(f => f.VideoFormat is not null && f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
            .OrderBy(f => Math.Abs((long)f.VideoFormat.Width * f.VideoFormat.Height - wanted))
            .ThenBy(f => Math.Abs(Rate(f) - fps))
            .ThenBy(f => string.Equals(f.Subtype, MediaEncodingSubtypes.Mjpg, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
    }

    private static double Rate(MediaFrameFormat format)
        => format.FrameRate is { Denominator: > 0 } rate ? rate.Numerator / (double)rate.Denominator : 30;

    private static string Describe(MediaFrameFormat format)
        => $"{format.VideoFormat.Width}×{format.VideoFormat.Height} {Rate(format):0} FPS {format.Subtype}";

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            using var reference = sender.TryAcquireLatestFrame();
            var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            SoftwareBitmap? converted = null;
            try
            {
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                var frame = converted ?? bitmap;
                if (frame.PixelWidth < Width || frame.PixelHeight < Height)
                {
                    SizeChanged = true;
                    _frameReady.Set();
                    return;
                }
                using var buffer = frame.LockBuffer(BitmapBufferAccessMode.Read);
                var plane = buffer.GetPlaneDescription(0);
                using var memory = buffer.CreateReference();
                memory.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out _);
                lock (_gate)
                {
                    if (_disposed) return;
                    _gpu.Context.UpdateSubresource(Texture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1), (nint)(data + plane.StartIndex), (uint)plane.Stride, 0);
                }
            }
            finally
            {
                converted?.Dispose();
                bitmap.Dispose();
            }
            Interlocked.Exchange(ref _lastTimestampUs, MediaClock.NowUs);
            _frameReady.Set();
        }
        catch (Exception exception) when (!_disposed)
        {
            Log.Debug($"Frame da câmera descartado: {exception.Message}");
        }
    }

    public bool WaitForFrame(TimeSpan timeout, out long timestampUs)
    {
        var got = _frameReady.WaitOne(timeout);
        timestampUs = Interlocked.Read(ref _lastTimestampUs);
        if (timestampUs == 0) timestampUs = MediaClock.NowUs;
        return got && !SizeChanged;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { _reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _reader.Dispose(); } catch { }
        }
        try { _capture.Dispose(); } catch { }
        Texture?.Dispose();
        _frameReady.Dispose();
        Log.Info("Câmera desligada.");
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
