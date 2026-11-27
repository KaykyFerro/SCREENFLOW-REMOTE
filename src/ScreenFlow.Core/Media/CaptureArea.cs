namespace ScreenFlow.Core.Media;

/// <summary>Um retângulo em pixels da origem (monitor ou janela).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Um retângulo em frações da origem (0 a 1), para valer em qualquer resolução: a mesma região continua certa quando o
/// monitor muda de resolução ou a janela muda de tamanho.
/// </summary>
public sealed record RelativeRect(double X, double Y, double Width, double Height)
{
    /// <summary>O retângulo arrumado: dentro de 0..1 e com tamanho. Null se não sobra nada.</summary>
    public RelativeRect? Clean()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)) return null;
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var right = Math.Clamp(X + Width, 0, 1);
        var bottom = Math.Clamp(Y + Height, 0, 1);
        return right - x < 0.01 || bottom - y < 0.01 ? null : new RelativeRect(x, y, right - x, bottom - y);
    }

    public PixelRect ToPixels(int width, int height)
        => new((int)Math.Round(X * width), (int)Math.Round(Y * height), (int)Math.Round(Width * width), (int)Math.Round(Height * height));

    /// <summary>A área onde o Windows mostra as notificações (canto de baixo à direita, acima da barra de tarefas).</summary>
    public static RelativeRect Notifications { get; } = new(0.74, 0.62, 0.26, 0.38);
}

/// <summary>
/// O que a captura da tela mostra (1.4.0): o cursor ou não, só uma região da origem e as áreas tapadas (de preto)
/// antes de codificar, então quem assiste nunca recebe o que está embaixo delas.
/// </summary>
public sealed record CaptureArea(bool ShowCursor = true, RelativeRect? Region = null, IReadOnlyList<RelativeRect>? Masks = null)
{
    public const int MaxMasks = 8;

    /// <summary>A menor região aceita (o encoder precisa de um tamanho mínimo).</summary>
    public const int MinRegionPixels = 128;

    public static CaptureArea Default { get; } = new();

    /// <summary>
    /// A região em pixels da origem: dentro dela, com largura e altura pares (o encoder exige) e pelo menos
    /// <see cref="MinRegionPixels"/>. Sem região (ou uma que cobre quase tudo), a origem inteira.
    /// </summary>
    public PixelRect RegionIn(int sourceWidth, int sourceHeight)
    {
        var whole = new PixelRect(0, 0, sourceWidth & ~1, sourceHeight & ~1);
        if (Region?.Clean() is not { } region || sourceWidth < MinRegionPixels || sourceHeight < MinRegionPixels) return whole;
        var pixels = region.ToPixels(sourceWidth, sourceHeight);
        var width = Math.Clamp(pixels.Width, MinRegionPixels, sourceWidth) & ~1;
        var height = Math.Clamp(pixels.Height, MinRegionPixels, sourceHeight) & ~1;
        var x = Math.Clamp(pixels.X, 0, sourceWidth - width);
        var y = Math.Clamp(pixels.Y, 0, sourceHeight - height);
        var result = new PixelRect(x, y, width, height);
        return result.Width >= whole.Width - 2 && result.Height >= whole.Height - 2 ? whole : result;
    }

    /// <summary>As áreas tapadas, em pixels da imagem que sai (depois do corte da região), cortadas nas bordas.</summary>
    public IReadOnlyList<PixelRect> MasksIn(int sourceWidth, int sourceHeight)
    {
        if (Masks is not { Count: > 0 } masks) return [];
        var region = RegionIn(sourceWidth, sourceHeight);
        var result = new List<PixelRect>();
        foreach (var mask in masks.Take(MaxMasks))
        {
            if (mask.Clean() is not { } clean) continue;
            // A máscara é relativa à origem inteira: passa para a região cortada.
            var pixels = clean.ToPixels(sourceWidth, sourceHeight);
            var left = Math.Max(pixels.X, region.X) - region.X;
            var top = Math.Max(pixels.Y, region.Y) - region.Y;
            var right = Math.Min(pixels.Right, region.Right) - region.X;
            var bottom = Math.Min(pixels.Bottom, region.Bottom) - region.Y;
            if (right - left >= 2 && bottom - top >= 2) result.Add(new PixelRect(left, top, right - left, bottom - top));
        }
        return result;
    }

    /// <summary>Em palavras, para a tela Transmissão.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Region?.Clean() is { } region) parts.Add($"região de {region.Width:P0} × {region.Height:P0} da tela");
        if (Masks is { Count: > 0 } masks) parts.Add(masks.Count == 1 ? "1 área tapada" : $"{masks.Count} áreas tapadas");
        if (!ShowCursor) parts.Add("sem o cursor");
        return parts.Count == 0 ? "Tela inteira, com o cursor." : string.Join(", ", parts) + ".";
    }
}
