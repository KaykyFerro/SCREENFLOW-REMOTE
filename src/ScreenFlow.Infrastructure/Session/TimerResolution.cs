using System.Runtime.InteropServices;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Infrastructure.Session;

/// <summary>
/// Relógio do Windows em 1 ms enquanto há transmissão ou player aberto. Sem isso as esperas curtas (o ritmo de envio,
/// o timer de 4 ms da remontagem) arredondam para 15,6 ms. Contado por referência: volta ao normal quando tudo fecha.
/// </summary>
public static class TimerResolution
{
    private static readonly object Gate = new();
    private static int _users;

    public static IDisposable Request()
    {
        lock (Gate)
        {
            if (_users++ == 0)
            {
                try { timeBeginPeriod(1); }
                catch (Exception exception) { Log.Warn($"Relógio de 1 ms indisponível: {exception.Message}"); }
            }
        }
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate)
            {
                if (--_users == 0)
                {
                    try { timeEndPeriod(1); } catch { }
                }
            }
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
