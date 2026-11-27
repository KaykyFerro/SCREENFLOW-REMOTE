namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Regras dos controles de quem assiste (1.2.0): o que o host aceita de cada pedido e o tamanho do que vai na rede.
/// Quem assiste só escolhe entre o que o host ofereceu: uma qualidade da lista, um comando que o app deixa, um app
/// da lista de apps com som.
/// </summary>
public static class RemoteControl
{
    /// <summary>A capa da música vai em JPEG pequeno; maior que isso, quem assiste ignora.</summary>
    public const int MaxCoverBytes = 64 * 1024;

    /// <summary>Apps na lista de "tirar do som" (os que estão tocando e os que já estão fora).</summary>
    public const int MaxApps = 24;

    /// <summary>Título, artista e nome do app são cortados aqui.</summary>
    public const int MaxText = 160;

    /// <summary>A qualidade pedida está entre as oferecidas e quem transmite deixa trocar.</summary>
    public static bool Accepts(RemoteStateMessage state, QualityRequestMessage request)
    {
        if (!state.CanChangeQuality || request.Fps <= 0 || string.IsNullOrEmpty(request.Resolution)) return false;
        if (state.CameraQualities.Length > 0)
            return state.CameraQualities.Any(q => string.Equals(q.Resolution, request.Resolution, StringComparison.OrdinalIgnoreCase) && q.Fps == request.Fps);
        return state.Resolutions.Contains(request.Resolution, StringComparer.OrdinalIgnoreCase) && state.FrameRates.Contains(request.Fps);
    }

    /// <summary>O comando é um dos conhecidos e o app que está tocando deixa (pular só se dá para pular, e assim por diante).</summary>
    public static bool Accepts(RemoteStateMessage state, MediaStateMessage media, MediaCommandMessage command)
    {
        if (!state.CanControlMedia || !media.Available || !MediaSeek.SameSession(media, command)) return false;
        return command.Command switch
        {
            MediaCommandMessage.PlayPause => media.CanPlayPause,
            MediaCommandMessage.Previous => media.CanPrevious,
            MediaCommandMessage.Next => media.CanNext,
            MediaCommandMessage.Seek => command.PositionSeconds >= 0 && MediaSeek.Range(media).Contains(command.PositionSeconds),
            MediaCommandMessage.SkipBy => !MediaSeek.Range(media).IsEmpty && double.IsFinite(command.DeltaSeconds) && command.DeltaSeconds != 0
                                          && Math.Abs(command.DeltaSeconds) <= MediaSeek.MaxSkipSeconds,
            _ => false,
        };
    }

    /// <summary>O pedido de apps tem um tamanho que faz sentido e quem transmite deixa escolher.</summary>
    public static bool Accepts(RemoteStateMessage state, AudioAppsRequestMessage request)
        => state.CanChooseApps && request.Excluded is { Length: <= MaxApps } excluded && excluded.All(exe => !string.IsNullOrWhiteSpace(exe) && exe.Length <= 260);

    /// <summary>
    /// Os apps que ficam fora depois do pedido. Só os apps da lista que o host mandou mudam (quem assiste não inventa um
    /// app); os que o host tirou e que não estão na lista continuam fora.
    /// </summary>
    public static IReadOnlyList<string> Exclusion(AudioAppsMessage published, IEnumerable<string> currentlyExcluded, AudioAppsRequestMessage request)
    {
        var listed = published.Apps.Select(a => a.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = (request.Excluded ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = currentlyExcluded.Where(exe => !listed.Contains(exe)).ToList();
        result.AddRange(published.Apps.Where(a => wanted.Contains(a.Exe)).Select(a => a.Exe));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A mesma música, no mesmo estado (sem olhar a posição): não precisa mandar de novo.</summary>
    public static bool SameTrack(MediaStateMessage a, MediaStateMessage b)
        => a.AppName == b.AppName && a.Title == b.Title && a.Artist == b.Artist && a.Playing == b.Playing
           && a.CanPrevious == b.CanPrevious && a.CanNext == b.CanNext && a.CanPlayPause == b.CanPlayPause && a.CanSeek == b.CanSeek
           && Math.Abs(a.DurationSeconds - b.DurationSeconds) < 1 && a.Cover == b.Cover
           && a.SessionKey == b.SessionKey && a.Live == b.Live && a.NoSeek == b.NoSeek
           && Math.Abs(a.SeekStartSeconds - b.SeekStartSeconds) < 2 && Math.Abs(a.SeekEndSeconds - b.SeekEndSeconds) < 2;

    /// <summary>
    /// Precisa mandar de novo: mudou a música ou o estado, ou a posição andou diferente do relógio (alguém mudou o ponto
    /// da música), ou faz tempo que a posição não é corrigida.
    /// </summary>
    public static bool ShouldPublish(MediaStateMessage? last, double lastSentAgoSeconds, MediaStateMessage next)
    {
        if (last is null || !SameTrack(last, next)) return true;
        if (!next.Available) return false;
        var expected = last.PositionSeconds + (last.Playing ? lastSentAgoSeconds : 0);
        return Math.Abs(expected - next.PositionSeconds) > 2 || lastSentAgoSeconds >= 15;
    }

    /// <summary>Corta os textos e tira uma capa grande demais, para a mensagem ficar pequena.</summary>
    public static MediaStateMessage Trim(MediaStateMessage media)
    {
        static string Cut(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= MaxText ? text : text[..MaxText];
        var cover = media.Cover is { Length: > 0 } c && c.Length <= MaxCoverBytes * 4 / 3 + 4 ? c : null;
        return media with
        {
            AppName = Cut(media.AppName),
            Title = Cut(media.Title),
            Artist = Cut(media.Artist),
            PositionSeconds = double.IsFinite(media.PositionSeconds) ? Math.Max(0, media.PositionSeconds) : 0,
            DurationSeconds = double.IsFinite(media.DurationSeconds) ? Math.Max(0, media.DurationSeconds) : 0,
            Cover = cover,
            SessionKey = media.SessionKey is { Length: > 0 and <= 64 } key ? key : null,
            SeekStartSeconds = double.IsFinite(media.SeekStartSeconds) ? Math.Max(0, media.SeekStartSeconds) : 0,
            SeekEndSeconds = double.IsFinite(media.SeekEndSeconds) ? Math.Max(0, media.SeekEndSeconds) : 0,
            NoSeek = media.NoSeek is { Length: > 0 and <= 32 } reason ? reason : null,
        };
    }

    /// <summary>A posição da música agora, a partir da que chegou e de quanto tempo passou (parada se estiver pausada).</summary>
    public static double PositionAt(MediaStateMessage media, double secondsSinceReceived)
    {
        var position = media.PositionSeconds + (media.Playing ? Math.Max(0, secondsSinceReceived) : 0);
        return media.DurationSeconds > 0 ? Math.Min(position, media.DurationSeconds) : position;
    }
}

/// <summary>
/// Limita os pedidos de cada pessoa que assiste (balde de fichas): até <paramref name="burst"/> seguidos, e depois
/// <paramref name="perSecond"/> por segundo. Um clique de cada vez nunca esbarra nisso; uma enxurrada, sim.
/// </summary>
public sealed class RequestLimiter(int burst = 8, double perSecond = 2)
{
    private readonly object _gate = new();
    private double _tokens = burst;
    private long _lastMs = long.MinValue;

    public bool TryTake(long nowMs)
    {
        lock (_gate)
        {
            if (_lastMs != long.MinValue && nowMs > _lastMs) _tokens = Math.Min(burst, _tokens + (nowMs - _lastMs) / 1000.0 * perSecond);
            if (_lastMs == long.MinValue || nowMs > _lastMs) _lastMs = nowMs;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
