using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Infrastructure.Session;

/// <summary>
/// Enquanto há transmissão ou player aberto (contado por referência; tudo volta ao normal quando o último fecha):
/// <list type="bullet">
/// <item>relógio do Windows em 1 ms: sem isso as esperas curtas (o ritmo de envio, o timer de 4 ms da remontagem)
/// arredondam para 15,6 ms e os pacotes saem em rajadas;</item>
/// <item>sem o modo de economia do Windows 11 (EcoQoS): com a janela minimizada ou na bandeja, o Windows baixaria a
/// velocidade do processo e ignoraria o relógio de 1 ms, justo quando o ScreenFlow transmite escondido ou a festa toca
/// com o player minimizado;</item>
/// <item>prioridade "acima do normal" para o processo, como nos apps de transmissão;</item>
/// <item>coleta de lixo sem pausas longas (<see cref="GCLatencyMode.SustainedLowLatency"/>).</item>
/// </list>
/// Cada ajuste é independente: se um falhar (Windows antigo, sem permissão), os outros continuam.
/// </summary>
public static class SessionPerformance
{
    private static readonly object Gate = new();
    private static int _users;
    private static bool _raisedPriority;
    private static GCLatencyMode? _previousLatency;

    public static IDisposable Request()
    {
        lock (Gate)
        {
            if (_users++ == 0) Apply();
        }
        return new Release();
    }

    private static void Apply()
    {
        try { timeBeginPeriod(1); }
        catch (Exception exception) { Log.Warn($"Relógio de 1 ms indisponível: {exception.Message}"); }

        // Windows 11 entende os dois; o Windows 10 só o primeiro (o segundo é recusado, então tenta de novo sem ele).
        if (!SetPowerThrottling(ExecutionSpeed | IgnoreTimerResolution, 0) && !SetPowerThrottling(ExecutionSpeed, 0))
            Log.Debug($"Modo de economia do Windows não mudou (erro {Marshal.GetLastWin32Error()}).");

        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass == ProcessPriorityClass.Normal)
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                _raisedPriority = true;
            }
        }
        catch (Exception exception) { Log.Debug($"Prioridade do processo não mudou: {exception.Message}"); }

        try
        {
            _previousLatency = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch (Exception exception) { Log.Debug($"Modo da coleta de lixo não mudou: {exception.Message}"); }
        Log.Info("Desempenho de transmissão ligado: relógio de 1 ms, sem economia de energia do Windows, prioridade acima do normal.");
    }

    private static void Restore()
    {
        try { timeEndPeriod(1); } catch { }
        // Máscaras zeradas: o Windows volta a decidir sozinho.
        SetPowerThrottling(0, 0);
        try
        {
            if (_raisedPriority)
            {
                using var process = Process.GetCurrentProcess();
                if (process.PriorityClass == ProcessPriorityClass.AboveNormal) process.PriorityClass = ProcessPriorityClass.Normal;
            }
        }
        catch { }
        _raisedPriority = false;
        try
        {
            if (_previousLatency is { } latency && GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency) GCSettings.LatencyMode = latency;
        }
        catch { }
        _previousLatency = null;
        Log.Info("Desempenho de transmissão desligado.");
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate)
            {
                if (--_users == 0) Restore();
            }
        }
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingVersion = 1, ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static bool SetPowerThrottling(uint control, uint state)
    {
        try
        {
            var info = new PowerThrottlingState { Version = PowerThrottlingVersion, ControlMask = control, StateMask = state };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref info, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception) { return false; } // Windows sem a função (anterior ao Windows 8)
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
