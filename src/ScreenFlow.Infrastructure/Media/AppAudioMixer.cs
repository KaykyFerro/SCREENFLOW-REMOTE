using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>
/// Som do PC menos alguns apps (e menos o ScreenFlow). O Windows só tira uma árvore de processos por captura, então cada
/// app com som é capturado separado (loopback de processo, "só este app e os filhos") e tudo é misturado aqui. O ritmo da
/// mistura vem de uma captura "tudo menos o ScreenFlow", que anda junto com o motor de áudio do Windows: cada pacote dela
/// puxa a mesma quantidade de áudio de cada app, então a mistura nunca adianta nem atrasa em relação ao som real.
/// A lista de apps é revista a cada segundo (um app que começa a tocar entra logo).
/// </summary>
public sealed class AppAudioMixer : IWaveIn
{
    private const int Channels = 2;
    private const int PrebufferFrames = 48_000 * 20 / 1000;   // 20 ms: diferença de chegada entre as capturas
    private const int MaxQueueFrames = 48_000 * 120 / 1000;   // acima de 120 ms guardados, o excesso sai
    private const int MaxSources = 24;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly HashSet<string> _muted;
    private readonly ProcessLoopbackCapture _clock;
    private readonly object _gate = new();
    private readonly Dictionary<int, Source> _sources = new();
    private readonly HashSet<int> _failed = new();
    private readonly ManualResetEventSlim _stopScan = new(false);
    private Thread? _scanner;
    private float[] _mix = [];
    private byte[] _bytes = [];
    private volatile bool _stopped;
    private bool _disposed;

    private AppAudioMixer(IEnumerable<string> mutedExes, ProcessLoopbackCapture clock)
    {
        _muted = mutedExes.Select(MutedApp.NormalizeExe).Where(e => e.Length > 0).ToHashSet();
        _clock = clock;
        _clock.DataAvailable += OnClock;
        _clock.RecordingStopped += OnClockStopped;
    }

    /// <summary>Abre a mistura. Lança se o Windows não tiver o loopback de processo.</summary>
    public static AppAudioMixer Open(IEnumerable<string> mutedExes) => new(mutedExes, ProcessLoopbackCapture.ExcludingProcess(Environment.ProcessId));

    public WaveFormat WaveFormat { get; set; } = ProcessLoopbackCapture.CaptureFormat;

    /// <summary>Executáveis sendo capturados agora (para o log e o autoteste).</summary>
    public IReadOnlyList<string> CapturedApps
    {
        get
        {
            lock (_gate) return _sources.Values.Select(s => s.Exe).Distinct().Order().ToList();
        }
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scanner is not null) return;
        Scan();
        _clock.StartRecording();
        _scanner = new Thread(ScanLoop) { IsBackground = true, Name = "ScreenFlow apps com som" };
        _scanner.Start();
    }

    public void StopRecording()
    {
        _stopped = true;
        _stopScan.Set();
        _clock.StopRecording();
        lock (_gate)
            foreach (var source in _sources.Values) source.Capture.StopRecording();
    }

    private void ScanLoop()
    {
        try
        {
            while (!_stopScan.Wait(ScanInterval) && !_stopped)
            {
                try { Scan(); }
                catch (Exception exception) { Log.Warn($"Não deu para rever os apps com som: {exception.Message}"); }
            }
        }
        catch (ObjectDisposedException) { } // a mistura fechou enquanto um app ainda abria
    }

    /// <summary>Confere quais apps têm som agora e abre ou fecha as capturas de cada um.</summary>
    private void Scan()
    {
        if (_stopped) return;
        var table = ProcessTable.Snapshot();
        var wanted = AppAudioPlan.Choose(AudioSessions.Scan().Keys, table, _ownProcessId, _muted).Take(MaxSources).ToHashSet();

        List<Source> removed;
        int[] added;
        lock (_gate)
        {
            removed = _sources.Values.Where(s => !wanted.Contains(s.ProcessId) || !s.Alive).ToList();
            foreach (var source in removed) _sources.Remove(source.ProcessId);
            added = wanted.Where(id => !_sources.ContainsKey(id) && !_failed.Contains(id)).ToArray();
        }
        foreach (var source in removed)
        {
            source.Capture.DataAvailable -= source.OnData;
            source.Capture.Dispose();
            Log.Info($"Som do PC: parou de capturar {source.Exe} ({source.ProcessId}).");
        }
        foreach (var id in added)
        {
            if (_stopped) return;
            var exe = table.TryGetValue(id, out var entry) ? entry.Exe : "?";
            try
            {
                var source = new Source(this, id, exe, ProcessLoopbackCapture.OnlyProcess(id));
                lock (_gate)
                {
                    if (_stopped) { source.Capture.Dispose(); return; }
                    _sources[id] = source;
                }
                source.Capture.StartRecording();
                Log.Info($"Som do PC: capturando {exe} ({id}).");
            }
            catch (Exception exception)
            {
                lock (_gate) _failed.Add(id);
                Log.Warn($"Som do PC: não deu para capturar {exe} ({id}): {exception.Message}");
            }
        }
    }

    private void OnClock(object? sender, WaveInEventArgs e)
    {
        var frames = e.BytesRecorded / (Channels * sizeof(float));
        if (frames <= 0 || _stopped) return;
        var length = frames * Channels;
        if (_mix.Length < length) _mix = new float[length * 2];
        var mix = _mix.AsSpan(0, length);
        mix.Clear();
        lock (_gate)
            foreach (var source in _sources.Values) source.Queue.MixInto(mix, frames);
        for (var i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        var bytes = length * sizeof(float);
        if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
        MemoryMarshal.AsBytes(mix).CopyTo(_bytes);
        DataAvailable?.Invoke(this, new WaveInEventArgs(_bytes, bytes));
    }

    private void OnClockStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) RecordingStopped?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        var scannerGone = _scanner is not { } scanner || scanner == Thread.CurrentThread || scanner.Join(2000);
        _clock.DataAvailable -= OnClock;
        _clock.RecordingStopped -= OnClockStopped;
        _clock.Dispose();
        List<Source> sources;
        lock (_gate)
        {
            sources = _sources.Values.ToList();
            _sources.Clear();
        }
        foreach (var source in sources)
        {
            source.Capture.DataAvailable -= source.OnData;
            source.Capture.Dispose();
        }
        if (scannerGone) _stopScan.Dispose();
    }

    /// <summary>A captura de um app e a fila dele na mistura.</summary>
    private sealed class Source
    {
        private readonly AppAudioMixer _owner;

        public Source(AppAudioMixer owner, int processId, string exe, ProcessLoopbackCapture capture)
        {
            _owner = owner;
            ProcessId = processId;
            Exe = exe;
            Capture = capture;
            Queue = new MixQueue(Channels, PrebufferFrames, MaxQueueFrames);
            Capture.DataAvailable += OnData;
            Capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is null) return;
                Alive = false;
                lock (_owner._gate) _owner._failed.Add(processId); // não tenta de novo a cada segundo
                Log.Warn($"Som do PC: a captura de {exe} ({processId}) parou: {e.Exception.Message}");
            };
        }

        public int ProcessId { get; }
        public string Exe { get; }
        public ProcessLoopbackCapture Capture { get; }
        public MixQueue Queue { get; }
        public bool Alive { get; private set; } = true;

        public void OnData(object? sender, WaveInEventArgs e)
        {
            var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded - e.BytesRecorded % (Channels * sizeof(float))));
            lock (_owner._gate) Queue.Push(samples);
        }
    }
}

/// <summary>Processos com sessão de som (tocando agora ou parados) em todas as saídas de som ligadas.</summary>
public static class AudioSessions
{
    /// <summary>Id do processo → se está tocando agora.</summary>
    public static Dictionary<int, bool> Scan()
    {
        var result = new Dictionary<int, bool>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            AudioSessionManager? manager = null;
            try
            {
                manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    try
                    {
                        var state = session.State;
                        if (state == AudioSessionState.AudioSessionStateExpired || session.IsSystemSoundsSession) continue;
                        var id = (int)session.GetProcessID;
                        result[id] = (result.TryGetValue(id, out var playing) && playing) || state == AudioSessionState.AudioSessionStateActive;
                    }
                    catch (Exception exception) when (exception is COMException or InvalidCastException) { }
                }
            }
            catch (Exception exception) { Log.Warn($"Não deu para ler as sessões de som de {SafeName(device)}: {exception.Message}"); }
            finally
            {
                try { manager?.Dispose(); } catch { }
                try { device.Dispose(); } catch { }
            }
        }
        return result;
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return "uma saída de som"; }
    }
}

/// <summary>Tabela de processos do Windows (Toolhelp): id, pai, executável e hora de início.</summary>
public static class ProcessTable
{
    public static Dictionary<int, ProcessEntry> Snapshot()
    {
        var table = new Dictionary<int, ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return table;
            do
            {
                var id = (int)entry.ProcessId;
                table[id] = new ProcessEntry(id, (int)entry.ParentProcessId, (entry.ExeFile ?? string.Empty).ToLowerInvariant(), StartTime(id));
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return table;
    }

    /// <summary>Caminho completo do executável (null se o Windows não deixar ler).</summary>
    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static long StartTime(int processId)
    {
        if (processId <= 4) return 0;
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == 0) return 0;
        try { return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : 0; }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>Um app aberto que pode ter o som tirado da transmissão.</summary>
public sealed record RunningApp(string Exe, string Name, string? Path, bool PlayingSound, byte[]? IconPng);

/// <summary>Lista os apps que dá para tirar da transmissão: os que têm som e os que têm janela aberta.</summary>
public static class AppAudioCatalog
{
    // Partes do Windows que têm janela mas não são apps de verdade.
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "textinputhost.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe",
        "startmenuexperiencehost.exe", "systemsettings.exe", "lockapp.exe", "dwm.exe", "widgets.exe", "gamebar.exe", "nvidia overlay.exe",
    };

    public static IReadOnlyList<RunningApp> List()
    {
        var own = Environment.ProcessId;
        var table = ProcessTable.Snapshot();
        var found = new Dictionary<string, (int ProcessId, bool Playing)>(StringComparer.OrdinalIgnoreCase);

        void Add(int processId, bool playing)
        {
            if (processId == own || !table.TryGetValue(processId, out var entry) || entry.Exe.Length == 0 || Hidden.Contains(entry.Exe)
                || entry.Exe == "screenflow.exe") return;
            found[entry.Exe] = found.TryGetValue(entry.Exe, out var known) ? (known.ProcessId, known.Playing || playing) : (processId, playing);
        }

        try
        {
            foreach (var (processId, playing) in AudioSessions.Scan()) Add(processId, playing);
        }
        catch (Exception exception) { Log.Warn($"Não deu para ler quem está tocando som: {exception.Message}"); }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != 0 && process.MainWindowTitle.Length > 0) Add(process.Id, false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        var apps = new List<RunningApp>();
        foreach (var (exe, (processId, playing)) in found)
        {
            var path = ProcessTable.ImagePath(processId);
            apps.Add(new RunningApp(exe, DisplayName(path, exe), path, playing, Icon(path)));
        }
        return apps.OrderByDescending(a => a.PlayingSound).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>O nome que o próprio app diz ("Spotify", "Google Chrome"), ou o do executável.</summary>
    public static string DisplayName(string? path, string exe)
    {
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                foreach (var candidate in new[] { info.FileDescription, info.ProductName })
                {
                    var name = candidate?.Trim();
                    if (!string.IsNullOrEmpty(name) && name.Length <= 60) return name;
                }
            }
            catch (Exception exception) { Log.Debug($"Sem o nome de {exe}: {exception.Message}"); }
        }
        return MutedApp.DefaultName(exe);
    }

    private static byte[]? Icon(string? path)
    {
        if (path is null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception exception)
        {
            Log.Debug($"Sem o ícone de {path}: {exception.Message}");
            return null;
        }
    }
}
