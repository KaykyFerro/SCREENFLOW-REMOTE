using Vortice.Direct3D11;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Fonte de captura na GPU. O último frame fica sempre em <see cref="Texture"/> (BGRA, tamanho fixo).
/// </summary>
public interface ICaptureSource : IDisposable
{
    string Backend { get; }
    int Width { get; }
    int Height { get; }
    ID3D11Texture2D Texture { get; }

    /// <summary>Espera um frame novo. Retorna false se não houve frame no tempo (tela parada).</summary>
    bool WaitForFrame(TimeSpan timeout, out long timestampUs);

    /// <summary>Disparado quando o tamanho da origem mudou e a captura precisa ser recriada.</summary>
    bool SizeChanged { get; }
}
