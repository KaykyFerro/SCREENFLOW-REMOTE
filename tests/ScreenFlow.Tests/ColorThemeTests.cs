using ScreenFlow.Core;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

public class ColorThemeTests
{
    public static TheoryData<string, bool> Themes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var theme in ColorThemes.All)
        {
            data.Add(theme.Id, true);
            data.Add(theme.Id, false);
        }
        return data;
    }

    [Fact]
    public void TenThemesOneOfEachColor()
    {
        Assert.Equal(10, ColorThemes.All.Count);
        Assert.Equal(ColorThemes.All.Count, ColorThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(ColorThemes.All.Count, ColorThemes.All.Select(t => t.Name).Distinct().Count());
        Assert.Same(ColorThemes.Default, ColorThemes.All[0]);
        // Cada tema numa cor diferente: os tons ficam a pelo menos 20 graus um do outro.
        var hues = ColorThemes.All.Select(t => ColorThemes.ToHsl(t.Accent).H).ToList();
        for (var i = 0; i < hues.Count; i++)
            for (var j = i + 1; j < hues.Count; j++)
            {
                var distance = Math.Abs(hues[i] - hues[j]);
                distance = Math.Min(distance, 360 - distance);
                Assert.True(distance >= 20, $"{ColorThemes.All[i].Name} e {ColorThemes.All[j].Name} estão a {distance:0} graus");
            }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryThemeKeepsTextReadable(string id, bool dark)
    {
        var palette = ColorThemes.Palette(ColorThemes.Resolve(id), dark);
        foreach (var key in ColorThemes.SolidKeys) Assert.True(palette.Solids.ContainsKey(key), $"{id}: falta {key}");
        foreach (var (key, stops) in ColorThemes.GradientStops) Assert.Equal(stops, palette.Gradients[key].Length);

        static void AtLeast(double minimum, uint a, uint b, string what)
            => Assert.True(ColorThemes.Contrast(a, b) >= minimum, $"{what}: contraste {ColorThemes.Contrast(a, b):0.00} (mínimo {minimum})");

        AtLeast(7, palette["Text"], palette["Background"], $"{id} texto no fundo");
        AtLeast(7, palette["Text"], palette["Surface"], $"{id} texto no cartão");
        AtLeast(7, palette["Text"], palette["SurfaceRaised"], $"{id} texto no cartão elevado");
        AtLeast(4.5, palette["TextMuted"], palette["Surface"], $"{id} texto secundário");
        AtLeast(4.5, palette["TextMuted"], palette["SurfaceRaised"], $"{id} texto secundário no cartão elevado");
        AtLeast(dark ? 3 : 4.5, palette["TextSubtle"], palette["Surface"], $"{id} texto apagado");
        AtLeast(4.5, palette["AccentText"], palette["Surface"], $"{id} texto em destaque");
        AtLeast(4.5, palette["AccentText"], palette["Background"], $"{id} texto em destaque no fundo");
        AtLeast(4.5, palette["OnAccent"], palette["Accent"], $"{id} texto do botão");
        AtLeast(4.5, palette["OnAccent"], palette["AccentHover"], $"{id} texto do botão (mouse em cima)");
        // As bordas aparecem (sem precisar de contraste de texto).
        Assert.True(ColorThemes.Contrast(palette["BorderStrong"], palette["Surface"]) >= 1.4, $"{id}: borda some no cartão");
    }

    [Fact]
    public void ThemesTintTheBackgroundWithTheirOwnColor()
    {
        var red = ColorThemes.Palette(ColorThemes.Resolve("vermelho"), dark: true)["Background"];
        var blue = ColorThemes.Palette(ColorThemes.Resolve("azul"), dark: true)["Background"];
        Assert.True(((red >> 16) & 0xFF) > (red & 0xFF), "o fundo do vermelho puxa para o vermelho");
        Assert.True((blue & 0xFF) > ((blue >> 16) & 0xFF), "o fundo do azul puxa para o azul");
        Assert.NotEqual(red, blue);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("menta", null)]
    [InlineData("Azul", "azul")]
    [InlineData(" rosa ", "rosa")]
    [InlineData("magenta-neon", null)]
    public void SettingsKeepOnlyKnownThemes(string? saved, string? expected)
    {
        var settings = new AppSettings { ColorTheme = saved };
        settings.Normalize();
        Assert.Equal(expected, settings.ColorTheme);
        Assert.Equal(expected ?? "menta", ColorThemes.Resolve(settings.ColorTheme).Id);
    }

    [Fact]
    public void ColorMathRoundTrips()
    {
        foreach (var theme in ColorThemes.All)
        {
            var (h, s, l) = ColorThemes.ToHsl(theme.Accent);
            var back = ColorThemes.FromHsl(h, s, l);
            Assert.True(Math.Abs((int)((back >> 16) & 0xFF) - (int)((theme.Accent >> 16) & 0xFF)) <= 1);
            Assert.True(Math.Abs((int)((back >> 8) & 0xFF) - (int)((theme.Accent >> 8) & 0xFF)) <= 1);
            Assert.True(Math.Abs((int)(back & 0xFF) - (int)(theme.Accent & 0xFF)) <= 1);
        }
        Assert.Equal(21, ColorThemes.Contrast(0xFF000000, 0xFFFFFFFF), 1);
        Assert.Equal(1, ColorThemes.Contrast(0xFF2EE6A6, 0xFF2EE6A6), 3);
    }
}

public class ServerSettingsTests
{
    [Fact]
    public void ServerSettingsAreCleaned()
    {
        var settings = new AppSettings
        {
            ServerRole = (ServerRole)42,
            ServerAddress = "   ",
            ServerPassword = "",
            HostedServerPassword = new string('x', 500),
            HostedServerName = "  Servidor\u0007 da Turma  ",
        };
        settings.Normalize();
        Assert.Equal(ServerRole.None, settings.ServerRole);
        Assert.Null(settings.ServerAddress);
        Assert.Null(settings.ServerPassword);
        Assert.Null(settings.HostedServerPassword);
        Assert.Equal("Servidor da Turma", settings.HostedServerName);
        Assert.True(settings.TransmitViaServer);
    }

    [Theory]
    [InlineData("100.101.102.103", "100.101.102.103", AppInfo.DefaultServerControlPort)]
    [InlineData(" servidor-casa ", "servidor-casa", AppInfo.DefaultServerControlPort)]
    [InlineData("servidor.tail1234.ts.net:48000", "servidor.tail1234.ts.net", 48000)]
    [InlineData("26.10.20.30:47810", "26.10.20.30", 47810)]
    public void ServerAddressSplitsHostAndPort(string address, string host, int port)
    {
        var settings = new AppSettings { ServerAddress = address };
        settings.Normalize();
        Assert.Equal((host, port), settings.ServerEndpoint());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://servidor")]
    [InlineData("servidor:99999")]
    [InlineData("servidor com espaço")]
    public void InvalidServerAddressHasNoEndpoint(string? address)
    {
        var settings = new AppSettings { ServerAddress = address };
        Assert.Null(settings.ServerEndpoint());
    }
}
