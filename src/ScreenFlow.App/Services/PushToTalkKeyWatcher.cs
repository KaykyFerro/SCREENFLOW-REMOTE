using System.Runtime.InteropServices;
using System.Windows.Input;

namespace ScreenFlow.App.Services;

/// <summary>
/// Lê a tecla de apertar para falar em qualquer app (1.4.0): confere o estado da tecla a cada 15 ms enquanto você está
/// na voz com o recurso ligado. Não registra atalho nem engole a tecla: ela continua funcionando no app em uso.
/// </summary>
internal sealed class PushToTalkKeyWatcher : IDisposable
{
    private readonly Action<bool> _changed;
    private System.Threading.Timer? _timer;
    private int _virtualKey;
    private bool _held;

    public PushToTalkKeyWatcher(Action<bool> changed) => _changed = changed;

    /// <summary>A tecla que vale agora, em texto para a tela ("F8", "Botão lateral 1 do mouse").</summary>
    public static string Describe(string key) => key switch
    {
        "XButton1" => "Botão lateral 1 do mouse",
        "XButton2" => "Botão lateral 2 do mouse",
        _ => key,
    };

    /// <summary>O código da tecla no Windows. 0 se o nome não é de uma tecla.</summary>
    public static int VirtualKey(string key) => key switch
    {
        "XButton1" => 0x05,
        "XButton2" => 0x06,
        _ => Enum.TryParse<Key>(key, out var parsed) && parsed != Key.None ? KeyInterop.VirtualKeyFromKey(parsed) : 0,
    };

    /// <summary>Começa (ou para, com <paramref name="key"/> null) de ler a tecla.</summary>
    public void Watch(string? key)
    {
        var vk = key is null ? 0 : VirtualKey(key);
        if (vk == _virtualKey && (_timer is not null) == (vk != 0)) return;
        _virtualKey = vk;
        _timer?.Dispose();
        _timer = null;
        if (_held)
        {
            _held = false;
            _changed(false);
        }
        if (vk != 0) _timer = new System.Threading.Timer(_ => Poll(), null, 0, 15);
    }

    private void Poll()
    {
        var vk = _virtualKey;
        if (vk == 0) return;
        var held = (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (held == _held) return;
        _held = held;
        _changed(held);
    }

    public void Dispose() => Watch(null);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
