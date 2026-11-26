namespace ScreenFlow.Core.Media;

/// <summary>
/// Liga um app dos controles de mídia do Windows (o id que ele usa ali: "Spotify.exe", "Chrome", "MSEdge",
/// "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") ao executável da janela ou do som que está sendo
/// transmitido, e dá um nome para mostrar.
/// </summary>
public static class MediaApps
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Spotify",
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["308046b0af4a39cb"] = "Firefox",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
        ["vlc"] = "VLC",
        ["discord"] = "Discord",
        ["deezer"] = "Deezer",
        ["tidal"] = "TIDAL",
        ["applemusic"] = "Apple Music",
        ["microsoft.zunemusic"] = "Reprodutor de Mídia",
        ["microsoft.zunevideo"] = "Filmes e TV",
        ["spotifyab.spotifymusic"] = "Spotify",
    };

    // Ids que não dizem o executável: o Firefox usa um hash do lugar onde foi instalado.
    private static readonly Dictionary<string, string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["308046b0af4a39cb"] = "firefox",
        ["6f193ccc56814779"] = "firefox",
        ["spotifyab.spotifymusic"] = "spotify",
        ["appleinc.applemusicwin"] = "applemusic",
    };

    /// <summary>
    /// A parte que identifica o app, em minúsculas: "Spotify.exe" → "spotify"; "C:\Apps\vlc.exe" → "vlc";
    /// "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "microsoft.zunemusic".
    /// </summary>
    public static string Key(string? appId)
    {
        var id = (appId ?? string.Empty).Trim();
        var bang = id.IndexOf('!');
        if (bang >= 0) id = id[..bang];
        id = id.Replace('/', '\\');
        id = id[(id.LastIndexOf('\\') + 1)..];
        // App da loja: "Pacote_8wekyb3d8bbwe" (o fim é o editor, sempre 13 letras e números).
        var underscore = id.LastIndexOf('_');
        if (underscore > 0 && id.Length - underscore - 1 == 13 && id[(underscore + 1)..].All(char.IsAsciiLetterOrDigit)) id = id[..underscore];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        return id.ToLowerInvariant();
    }

    /// <summary>Nome para mostrar: "Spotify", "Google Chrome", ou o id arrumado.</summary>
    public static string DisplayName(string? appId)
    {
        var key = Key(appId);
        if (key.Length == 0) return "Mídia";
        if (Names.TryGetValue(key, out var name)) return name;
        var last = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
        return last.Length == 0 ? key : char.ToUpperInvariant(last[0]) + last[1..];
    }

    /// <summary>O app dos controles de mídia é o executável <paramref name="exe"/> ("Chrome" e "chrome.exe", por exemplo)?</summary>
    public static bool Matches(string? appId, string? exe)
    {
        var key = Key(appId);
        var name = Key(exe);
        if (key.Length == 0 || name.Length == 0) return false;
        if (Executables.TryGetValue(key, out var known)) key = known;
        return key == name || key.StartsWith(name + ".", StringComparison.Ordinal);
    }

    /// <summary>"3:07" ou "1:02:45".</summary>
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }
}

/// <summary>
/// Onde a música ou o vídeo está agora. O app só avisa a posição de vez em quando (o Spotify, a cada troca de faixa ou
/// pausa), então, tocando, ela anda com o relógio a partir do último aviso, sem passar do fim.
/// </summary>
public readonly record struct PlaybackTimeline(TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAt, bool Playing)
{
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var position = Position;
        if (Playing && now > UpdatedAt) position += now - UpdatedAt;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }
}
