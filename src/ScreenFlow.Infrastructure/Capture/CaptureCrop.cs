using ScreenFlow.Core.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Leva o frame capturado para a textura de trabalho só com a região escolhida e com as áreas tapadas pintadas de preto
/// (1.4.0). Tudo na GPU: o corte é uma cópia de um pedaço, e cada máscara é uma cópia de um pedaço de uma textura preta.
/// O que fica embaixo de uma máscara nunca chega ao encoder.
/// </summary>
public sealed class CaptureCrop : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly PixelRect _region;
    private readonly IReadOnlyList<PixelRect> _masks;
    private readonly ID3D11Texture2D? _black;

    public CaptureCrop(GpuDevice gpu, CaptureArea area, int sourceWidth, int sourceHeight)
    {
        _gpu = gpu;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        _region = area.RegionIn(sourceWidth, sourceHeight);
        _masks = area.MasksIn(sourceWidth, sourceHeight);
        if (_masks.Count > 0)
        {
            var width = _masks.Max(m => m.Width);
            var height = _masks.Max(m => m.Height);
            _black = gpu.CreateTexture(width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
            using var view = gpu.Device.CreateRenderTargetView(_black);
            gpu.Context.ClearRenderTargetView(view, new Color4(0, 0, 0, 1));
        }
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int Width => _region.Width;
    public int Height => _region.Height;
    public bool IsWhole => _region.X == 0 && _region.Y == 0 && _masks.Count == 0;
    public int MaskCount => _masks.Count;

    /// <summary>Copia a região de <paramref name="source"/> para <paramref name="target"/> e tapa as máscaras.</summary>
    public void Copy(ID3D11Texture2D source, ID3D11Texture2D target)
    {
        var context = _gpu.Context;
        context.CopySubresourceRegion(target, 0, 0, 0, 0, source, 0,
            new Box(_region.X, _region.Y, 0, _region.Right, _region.Bottom, 1));
        if (_black is null) return;
        foreach (var mask in _masks)
            context.CopySubresourceRegion(target, 0, (uint)mask.X, (uint)mask.Y, 0, _black, 0, new Box(0, 0, 0, mask.Width, mask.Height, 1));
    }

    public void Dispose() => _black?.Dispose();
}
