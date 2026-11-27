namespace ScreenFlow.Core.Settings;

/// <summary>Uma cor do tema (1.4.1): o id guardado nas configurações, o nome na tela e a cor de destaque (0xAARRGGBB).</summary>
public sealed record ColorTheme(string Id, string Name, uint Accent);

/// <summary>
/// As cores de um tema já montadas: as superfícies, o texto e o destaque (0xAARRGGBB). Os degradês têm uma cor por
/// ponto, na ordem dos pontos do degradê que elas substituem.
/// </summary>
public sealed record ThemePalette(IReadOnlyDictionary<string, uint> Solids, IReadOnlyDictionary<string, uint[]> Gradients)
{
    public uint this[string key] => Solids[key];
}

/// <summary>
/// Os 10 temas de cor (1.4.1), um de cada cor, escolhidos por cada pessoa no próprio PC (não vão para a sala). Cada um
/// pinta o destaque (botões, seleção, degradês) e tinge o fundo com a mesma cor, no modo escuro e no claro. A menta é a
/// de sempre: com ela o app fica igual às versões antigas.
/// </summary>
public static class ColorThemes
{
    public static readonly ColorTheme Default = new("menta", "Menta", 0xFF2EE6A6);

    public static readonly IReadOnlyList<ColorTheme> All =
    [
        Default,
        new("ciano", "Ciano", 0xFF22D3EE),
        new("azul", "Azul", 0xFF4F8DFF),
        new("violeta", "Violeta", 0xFF8B6CFF),
        new("roxo", "Roxo", 0xFFC062FF),
        new("rosa", "Rosa", 0xFFFF4D8D),
        new("vermelho", "Vermelho", 0xFFFF5A5A),
        new("laranja", "Laranja", 0xFFFF7F3A),
        new("amarelo", "Amarelo", 0xFFFFD43B),
        new("verde", "Verde", 0xFF7AD94A),
    ];

    /// <summary>As chaves de cor sólida que um tema troca (as outras, como as de estado e do player, ficam como estão).</summary>
    public static readonly IReadOnlyList<string> SolidKeys =
    [
        "Background", "Sidebar", "Surface", "SurfaceRaised", "SurfaceHover", "SurfaceSunken", "Border", "BorderStrong", "Divider",
        "Text", "TextMuted", "TextSubtle",
        "Accent", "AccentHover", "AccentPressed", "AccentText", "OnAccent", "AccentSoft", "AccentBorder", "Overlay",
    ];

    /// <summary>Os degradês que um tema troca, com quantos pontos cada um tem nos dicionários de tema.</summary>
    public static readonly IReadOnlyDictionary<string, int> GradientStops = new Dictionary<string, int>
    {
        ["AccentGradient"] = 2,
        ["AccentGradientHover"] = 2,
        ["BrandGradient"] = 3,
        ["PageGlow"] = 3,
    };

    /// <summary>O tema com este id (null: id desconhecido ou vazio).</summary>
    public static ColorTheme? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>O tema com este id, ou a menta.</summary>
    public static ColorTheme Resolve(string? id) => Find(id) ?? Default;

    /// <summary>As cores do tema no modo escuro ou no claro.</summary>
    public static ThemePalette Palette(ColorTheme theme, bool dark)
    {
        var accent = theme.Accent | 0xFF000000;
        var (hue, saturation, _) = ToHsl(accent);
        // Amarelo e verde ficam com cara de oliva quando o fundo é muito tingido: tinge menos.
        var tint = hue is >= 40 and <= 130 ? 0.22 : 0.36;
        var solids = new Dictionary<string, uint>();
        var gradients = new Dictionary<string, uint[]>();
        var neighbor = FromHsl(hue + 28, Math.Min(1, saturation), Lightness(accent));
        var far = FromHsl(hue + 85, Math.Min(1, saturation * 0.9), Math.Clamp(Lightness(accent), 0.55, 0.7));
        if (dark)
        {
            solids["Background"] = FromHsl(hue, tint, 0.065);
            solids["Sidebar"] = FromHsl(hue, tint, 0.085);
            solids["Surface"] = FromHsl(hue, tint, 0.125);
            solids["SurfaceRaised"] = FromHsl(hue, tint * 0.95, 0.165);
            solids["SurfaceHover"] = FromHsl(hue, tint * 0.95, 0.205);
            solids["SurfaceSunken"] = FromHsl(hue, tint, 0.095);
            solids["Border"] = FromHsl(hue, tint * 0.9, 0.205);
            solids["BorderStrong"] = FromHsl(hue, tint * 0.9, 0.29);
            solids["Divider"] = FromHsl(hue, tint * 0.95, 0.16);
            solids["Text"] = FromHsl(hue, 0.6, 0.97);
            solids["TextMuted"] = FromHsl(hue, 0.24, 0.75);
            solids["TextSubtle"] = FromHsl(hue, 0.14, 0.55);
            solids["Accent"] = accent;
            solids["AccentHover"] = Shift(accent, +0.08);
            solids["AccentPressed"] = Shift(accent, -0.08);
            solids["AccentText"] = EnsureContrast(Shift(accent, +0.04), solids["Surface"], 4.5, lighter: true);
            solids["Overlay"] = WithAlpha(FromHsl(hue, tint, 0.04), 0xB3);
            gradients["AccentGradient"] = [Shift(accent, +0.05), neighbor];
            gradients["AccentGradientHover"] = [Shift(accent, +0.13), Shift(neighbor, +0.08)];
            gradients["BrandGradient"] = [accent, neighbor, far];
            gradients["PageGlow"] = [WithAlpha(accent, 0x1A), WithAlpha(neighbor, 0x0A), WithAlpha(solids["Background"], 0x00)];
        }
        else
        {
            solids["Background"] = FromHsl(hue, tint * 0.9, 0.93);
            solids["Sidebar"] = 0xFFFFFFFF;
            solids["Surface"] = 0xFFFFFFFF;
            solids["SurfaceRaised"] = FromHsl(hue, tint, 0.965);
            solids["SurfaceHover"] = FromHsl(hue, tint, 0.925);
            solids["SurfaceSunken"] = FromHsl(hue, tint * 0.9, 0.93);
            solids["Border"] = FromHsl(hue, tint * 0.8, 0.85);
            solids["BorderStrong"] = FromHsl(hue, tint * 0.7, 0.755);
            solids["Divider"] = FromHsl(hue, tint * 0.8, 0.895);
            solids["Text"] = FromHsl(hue, 0.5, 0.1);
            solids["TextMuted"] = FromHsl(hue, 0.22, 0.34);
            solids["TextSubtle"] = EnsureContrast(FromHsl(hue, 0.14, 0.5), solids["SurfaceRaised"], 4.5, lighter: false);
            solids["Accent"] = accent;
            solids["AccentHover"] = Shift(accent, +0.06);
            solids["AccentPressed"] = Shift(accent, -0.07);
            solids["AccentText"] = EnsureContrast(Shift(accent, -0.12), solids["Background"], 4.5, lighter: false);
            solids["Overlay"] = WithAlpha(solids["Background"], 0x99);
            gradients["AccentGradient"] = [accent, neighbor];
            gradients["AccentGradientHover"] = [Shift(accent, +0.07), Shift(neighbor, +0.07)];
            gradients["BrandGradient"] = [Shift(accent, -0.08), Shift(neighbor, -0.08), Shift(far, -0.08)];
            gradients["PageGlow"] = [WithAlpha(accent, 0x26), WithAlpha(neighbor, 0x10), WithAlpha(solids["Background"], 0x00)];
        }
        // O texto em cima do destaque (botões): escuro no tom do tema ou branco, o que contrastar mais.
        var darkInk = FromHsl(hue, 0.8, 0.08);
        solids["OnAccent"] = Contrast(darkInk, accent) >= Contrast(0xFFFFFFFF, accent) ? darkInk : 0xFFFFFFFF;
        solids["AccentSoft"] = WithAlpha(accent, dark ? (byte)0x1F : (byte)0x26);
        solids["AccentBorder"] = WithAlpha(accent, dark ? (byte)0x66 : (byte)0x80);
        return new ThemePalette(solids, gradients);
    }

    // ───────────────────────── Cores ─────────────────────────

    /// <summary>Contraste entre duas cores (WCAG 2): 1 (iguais) a 21 (preto e branco). A transparência é ignorada.</summary>
    public static double Contrast(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Luminância relativa (WCAG 2), de 0 a 1.</summary>
    public static double Luminance(uint argb)
    {
        static double Channel(uint value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((argb >> 16) & 0xFF) + 0.7152 * Channel((argb >> 8) & 0xFF) + 0.0722 * Channel(argb & 0xFF);
    }

    /// <summary>Clareia (ou escurece) a cor até ter o contraste pedido contra o fundo. Mantém o tom.</summary>
    public static uint EnsureContrast(uint color, uint background, double ratio, bool lighter)
    {
        var (h, s, l) = ToHsl(color);
        var alpha = color & 0xFF000000;
        for (var i = 0; i < 100 && Contrast(color, background) < ratio; i++)
        {
            l = Math.Clamp(l + (lighter ? 0.01 : -0.01), 0, 1);
            color = (FromHsl(h, s, l) & 0x00FFFFFF) | alpha;
            if (l is 0 or 1) break;
        }
        return color;
    }

    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>Mais clara (positivo) ou mais escura (negativo), na mesma cor.</summary>
    public static uint Shift(uint argb, double lightness)
    {
        var (h, s, l) = ToHsl(argb);
        return (FromHsl(h, s, Math.Clamp(l + lightness, 0, 1)) & 0x00FFFFFF) | (argb & 0xFF000000);
    }

    public static double Lightness(uint argb) => ToHsl(argb).L;

    public static (double H, double S, double L) ToHsl(uint argb)
    {
        var r = ((argb >> 16) & 0xFF) / 255.0;
        var g = ((argb >> 8) & 0xFF) / 255.0;
        var b = (argb & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    /// <summary>Cor opaca a partir do tom (graus), saturação e luminosidade (0 a 1).</summary>
    public static uint FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360 / 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        double r, g, b;
        if (saturation < 1e-9) r = g = b = lightness;
        else
        {
            var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            var p = 2 * lightness - q;
            r = HueToRgb(p, q, hue + 1.0 / 3);
            g = HueToRgb(p, q, hue);
            b = HueToRgb(p, q, hue - 1.0 / 3);
        }
        static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
        return 0xFF000000 | (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
