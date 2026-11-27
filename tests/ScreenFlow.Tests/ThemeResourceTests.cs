using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>
/// Confere os recursos do tema do app WPF lendo os .xaml e .cs como texto (roda em qualquer SO, sem abrir janela).
/// Um recurso com o nome errado ou do tipo errado só quebra quando a tela abre no Windows, então é melhor pegar aqui.
/// </summary>
public class ThemeResourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex MarkupReference = new(@"\{(StaticResource|DynamicResource)\s+(?:ResourceKey=)?([^}\s,]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex CodeResourceReference = new(@"SetResourceReference\(\s*(?:[\w.]+\.)?(\w+)Property\s*,\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CodeFindResource = new(@"(\(\s*(?:[\w.]+\.)?(\w+)\s*\)\s*)?(?:\w+\.)*FindResource\(\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> PropertyKinds = new(StringComparer.Ordinal)
    {
        ["Background"] = "Brush", ["BorderBrush"] = "Brush", ["Foreground"] = "Brush", ["Fill"] = "Brush", ["Stroke"] = "Brush",
        ["CaretBrush"] = "Brush", ["SelectionBrush"] = "Brush", ["OpacityMask"] = "Brush",
        ["Style"] = "Style", ["BasedOn"] = "Style", ["FocusVisualStyle"] = "Style", ["ItemContainerStyle"] = "Style",
        ["CornerRadius"] = "CornerRadius", ["Padding"] = "Thickness", ["Margin"] = "Thickness", ["BorderThickness"] = "Thickness",
        ["FontFamily"] = "FontFamily", ["FontSize"] = "Double", ["Effect"] = "Effect", ["Converter"] = "Converter",
    };

    private static string AppFolder
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenFlow.sln"))) directory = directory.Parent;
            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "ScreenFlow.App");
        }
    }

    private static IEnumerable<string> Files(string pattern)
        => Directory.EnumerateFiles(AppFolder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Relative(string file) => Path.GetRelativePath(AppFolder, file).Replace('\\', '/');

    /// <summary>Recursos do app (dicionários do tema e App.xaml): nome → tipos de elemento que usam esse nome.</summary>
    private static Dictionary<string, HashSet<string>> AppResources(out Dictionary<string, Dictionary<string, string>> byFile)
    {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        byFile = [];
        foreach (var file in Files("*.xaml"))
        {
            var relative = Relative(file);
            var global = relative.StartsWith("Themes/", StringComparison.Ordinal) || relative == "App.xaml";
            var own = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in XDocument.Load(file).Descendants())
            {
                var key = (string?)element.Attribute(Xaml + "Key");
                if (key is null) continue;
                own[key] = element.Name.LocalName;
                if (global) (keys.TryGetValue(key, out var set) ? set : keys[key] = []).Add(element.Name.LocalName);
            }
            byFile[relative] = own;
        }
        return keys;
    }

    private static string Kind(string elementName) => elementName switch
    {
        _ when elementName.EndsWith("Brush", StringComparison.Ordinal) => "Brush",
        _ when elementName.EndsWith("Effect", StringComparison.Ordinal) => "Effect",
        _ when elementName.EndsWith("Converter", StringComparison.Ordinal) => "Converter",
        _ => elementName,
    };

    /// <summary>Todas as referências a recursos: arquivo, propriedade e nome do recurso.</summary>
    private static List<(string Where, string Property, string Key)> References()
    {
        var references = new List<(string, string, string)>();
        foreach (var file in Files("*.xaml"))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    foreach (Match match in MarkupReference.Matches(attribute.Value))
                    {
                        var key = match.Groups[2].Value;
                        if (key.StartsWith('{')) continue; // {x:Type ...} e {x:Static ...}
                        var property = attribute.Name.LocalName;
                        if (element.Name.LocalName == "Setter" && property == "Value") property = (string?)element.Attribute("Property") ?? property;
                        var nested = Regex.Match(attribute.Value, @"(\w+)=\{(?:StaticResource|DynamicResource)\s+" + Regex.Escape(key));
                        if (nested.Success) property = nested.Groups[1].Value;
                        references.Add(($"{Relative(file)} <{element.Name.LocalName}>", property[(property.LastIndexOf('.') + 1)..], key));
                    }
                }
            }
        }
        foreach (var file in Files("*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in CodeResourceReference.Matches(text))
                references.Add(($"{Relative(file)}:{Line(text, match.Index)}", match.Groups[1].Value, match.Groups[2].Value));
            foreach (Match match in CodeFindResource.Matches(text))
                references.Add(($"{Relative(file)}:{Line(text, match.Index)}", match.Groups[2].Success ? match.Groups[2].Value : string.Empty, match.Groups[3].Value));
        }
        return references;
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>(1.4.1) As cores do tema pintam por cima de chaves que existem nos dois temas, com o mesmo formato.</summary>
    [Fact]
    public void ColorThemesPaintKeysThatBothThemesHave()
    {
        foreach (var file in new[] { "Themes/Dark.xaml", "Themes/Light.xaml" })
        {
            var elements = XDocument.Load(Path.Combine(AppFolder, file)).Descendants()
                .Where(e => e.Attribute(Xaml + "Key") is not null)
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);
            foreach (var key in ScreenFlow.Core.Settings.ColorThemes.SolidKeys)
                Assert.True(elements.TryGetValue(key, out var solid) && solid.Name.LocalName == "SolidColorBrush", $"{file}: {key} precisa ser um SolidColorBrush");
            foreach (var (key, stops) in ScreenFlow.Core.Settings.ColorThemes.GradientStops)
            {
                Assert.True(elements.TryGetValue(key, out var gradient) && gradient.Name.LocalName.EndsWith("GradientBrush", StringComparison.Ordinal),
                    $"{file}: {key} precisa ser um gradiente");
                Assert.Equal(stops, gradient!.Descendants().Count(d => d.Name.LocalName == "GradientStop"));
            }
        }
    }

    [Fact]
    public void EachResourceNameHasOneType()
    {
        var keys = AppResources(out _);
        var mixed = keys.Where(k => k.Value.Select(Kind).Distinct().Count() > 1).Select(k => $"{k.Key}: {string.Join(", ", k.Value)}").ToList();
        Assert.True(mixed.Count == 0, "Recursos com o mesmo nome e tipos diferentes (o WPF pega o último): " + string.Join("; ", mixed));
    }

    [Fact]
    public void DarkAndLightThemesDefineTheSameResources()
    {
        AppResources(out var byFile);
        var dark = byFile["Themes/Dark.xaml"];
        var light = byFile["Themes/Light.xaml"];
        var missing = dark.Keys.Except(light.Keys).Select(k => $"{k} só no escuro")
            .Concat(light.Keys.Except(dark.Keys).Select(k => $"{k} só no claro"))
            .Concat(dark.Keys.Intersect(light.Keys).Where(k => Kind(dark[k]) != Kind(light[k])).Select(k => $"{k}: {dark[k]} × {light[k]}"))
            .ToList();
        Assert.True(missing.Count == 0, string.Join("; ", missing));
    }

    [Fact]
    public void EveryReferencedResourceExistsWithTheRightType()
    {
        var keys = AppResources(out var byFile);
        var local = byFile.Values.SelectMany(f => f).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToHashSet());
        var problems = new List<string>();
        var references = References();
        foreach (var (where, property, key) in references)
        {
            if (!keys.TryGetValue(key, out var types) && !local.TryGetValue(key, out types))
            {
                problems.Add($"{where}: '{key}' não existe");
                continue;
            }
            if (PropertyKinds.TryGetValue(property, out var expected) && !types.Select(Kind).Contains(expected))
                problems.Add($"{where}: {property} usa '{key}', que é {string.Join("/", types)} e não {expected}");
        }
        Assert.True(references.Count > 300, $"Só {references.Count} referências encontradas: a leitura dos arquivos mudou?");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
