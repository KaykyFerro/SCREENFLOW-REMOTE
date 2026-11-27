namespace ScreenFlow.Core.Protocol;

/// <summary>O trecho do vídeo em que dá para buscar, em segundos. Vazio: não dá para buscar.</summary>
public readonly record struct SeekRange(double Start, double End)
{
    public static SeekRange Empty { get; } = new(0, 0);

    public bool IsEmpty => !(End > Start) || !double.IsFinite(Start) || !double.IsFinite(End);

    public double Length => IsEmpty ? 0 : End - Start;

    public double Clamp(double seconds)
    {
        if (IsEmpty) return Start;
        if (!double.IsFinite(seconds)) return Start;
        return Math.Clamp(seconds, Start, End);
    }

    /// <summary>Está dentro do trecho, com uma folga para o relógio de quem assiste estar um pouco adiantado.</summary>
    public bool Contains(double seconds, double slack = 1)
        => !IsEmpty && double.IsFinite(seconds) && seconds >= Start - slack && seconds <= End + slack;
}

/// <summary>
/// Minutagem do vídeo transmitido (1.4.0): o trecho em que dá para buscar, o que o host aceita, como conferir se a busca
/// aconteceu e os textos da interface. A busca é sempre no app de origem, no PC de quem transmite: quem assiste vê o
/// mesmo vídeo no ponto novo porque é a tela do host que muda, não um player local.
/// </summary>
public static class MediaSeek
{
    /// <summary>Quanto os botões de avançar e voltar andam.</summary>
    public const double SkipSeconds = 10;

    /// <summary>O maior salto aceito num pedido de avançar ou voltar.</summary>
    public const double MaxSkipSeconds = 120;

    /// <summary>A posição lida depois da busca pode estar até aqui do pedido (o app arredonda e o vídeo continua andando).</summary>
    public const double ConfirmTolerance = 2.5;

    /// <summary>Sem resposta em tanto tempo, quem assiste mostra que a busca não foi confirmada.</summary>
    public const long TimeoutMs = 6000;

    // Como o comando terminou (MediaCommandResultMessage.Outcome).
    public const string OutcomeDone = "done";
    public const string OutcomeRejected = "rejected";
    public const string OutcomeUnconfirmed = "unconfirmed";
    public const string OutcomeUnavailable = "unavailable";
    public const string OutcomeStale = "stale";

    // Por que não dá para buscar (MediaStateMessage.NoSeek).
    public const string NoSeekUnsupported = "unsupported";
    public const string NoSeekNoTimeline = "no-timeline";
    public const string NoSeekUnbound = "unbound";

    /// <summary>
    /// O trecho em que dá para buscar: o que o app informou (ao vivo, por exemplo, só os últimos minutos) ou, sem isso, do
    /// começo até a duração. Vazio se o app não deixa buscar ou não informa a duração.
    /// </summary>
    public static SeekRange Range(MediaStateMessage media)
    {
        if (!media.Available || !media.CanSeek) return SeekRange.Empty;
        if (media.SeekEndSeconds > media.SeekStartSeconds && double.IsFinite(media.SeekStartSeconds) && double.IsFinite(media.SeekEndSeconds))
            return new SeekRange(Math.Max(0, media.SeekStartSeconds), media.SeekEndSeconds);
        return media.DurationSeconds > 0 && double.IsFinite(media.DurationSeconds) ? new SeekRange(0, media.DurationSeconds) : SeekRange.Empty;
    }

    /// <summary>
    /// O trecho de busca a partir do que o Windows informa do app: o começo e o fim da busca (MinSeekTime e MaxSeekTime),
    /// já contados a partir do começo da mídia. Sem esses valores (ou com valores sem sentido), usa a duração.
    /// </summary>
    public static SeekRange FromTimeline(double durationSeconds, double minSeekSeconds, double maxSeekSeconds)
    {
        if (double.IsFinite(minSeekSeconds) && double.IsFinite(maxSeekSeconds) && maxSeekSeconds > minSeekSeconds && maxSeekSeconds > 0)
        {
            var start = Math.Max(0, minSeekSeconds);
            var end = durationSeconds > 0 ? Math.Min(maxSeekSeconds, durationSeconds) : maxSeekSeconds;
            if (end > start) return new SeekRange(start, end);
        }
        return durationSeconds > 0 && double.IsFinite(durationSeconds) ? new SeekRange(0, durationSeconds) : SeekRange.Empty;
    }

    /// <summary>O trecho cobre só parte da mídia (ao vivo, ou um app que limita a busca).</summary>
    public static bool IsPartial(MediaStateMessage media)
    {
        var range = Range(media);
        return !range.IsEmpty && (range.Start > 0.5 || (media.DurationSeconds > 0 && range.End < media.DurationSeconds - 0.5) || media.Live);
    }

    /// <summary>Para onde o comando leva, a partir da posição de agora no host. Null se o comando não é de busca.</summary>
    public static double? Target(MediaStateMessage media, MediaCommandMessage command, double positionNow)
    {
        var range = Range(media);
        if (range.IsEmpty) return null;
        return command.Command switch
        {
            MediaCommandMessage.Seek => range.Clamp(command.PositionSeconds),
            MediaCommandMessage.SkipBy => range.Clamp(positionNow + command.DeltaSeconds),
            _ => null,
        };
    }

    /// <summary>A posição lida no app confirma a busca.</summary>
    public static bool Confirms(double target, double observed, double tolerance = ConfirmTolerance)
        => double.IsFinite(observed) && Math.Abs(observed - target) <= tolerance;

    /// <summary>O pedido de quem assiste é para a sessão de mídia que o host está mostrando agora (hosts e viewers antigos não mandam chave).</summary>
    public static bool SameSession(MediaStateMessage media, MediaCommandMessage command)
        => command.SessionKey is null || media.SessionKey is null || string.Equals(command.SessionKey, media.SessionKey, StringComparison.Ordinal);

    /// <summary>Por que a barra está desligada, para mostrar na interface. Null quando dá para buscar.</summary>
    public static string? Explain(MediaStateMessage media)
    {
        if (!media.Available) return null;
        if (!Range(media).IsEmpty) return null;
        return media.NoSeek switch
        {
            NoSeekUnbound => "Quem transmite ainda não escolheu qual app controlar.",
            NoSeekNoTimeline => "Esse app não informa a posição e a duração, então não dá para mudar o ponto.",
            _ when media.DurationSeconds <= 0 && !media.CanSeek => "Esse app não informa a posição e a duração, então não dá para mudar o ponto.",
            _ => "Esse app não deixa mudar o ponto pelos controles de mídia do Windows.",
        };
    }

    /// <summary>O texto de como o comando terminou.</summary>
    public static string Describe(string outcome) => outcome switch
    {
        OutcomeDone => "Pronto: todo mundo está no ponto novo.",
        OutcomeRejected => "O app recusou a mudança de ponto.",
        OutcomeUnconfirmed => "O app não confirmou a mudança de ponto.",
        OutcomeStale => "O vídeo mudou antes do pedido chegar. Tente de novo.",
        _ => "Não deu para mudar o ponto agora.",
    };

    /// <summary>O comando aconteceu de verdade (e, na busca, a posição nova foi conferida).</summary>
    public static bool Succeeded(string outcome) => outcome == OutcomeDone;
}

public enum SeekPhase { Idle, Pending, Done, Failed }

/// <summary>
/// O estado da busca do lado de quem assiste (ou do host, na tela Transmissão): um pedido por vez, sem repetir o mesmo
/// ponto enquanto ele está em andamento, resolvido pela resposta do host ou, com hosts antigos, pela posição que chega
/// no estado da mídia. Sem resposta em <see cref="MediaSeek.TimeoutMs"/>, falha: nunca finge que a busca funcionou.
/// </summary>
public sealed class SeekTracker
{
    private long _nextId;
    private long _startedMs;
    private long _phaseAtMs;

    public SeekPhase Phase { get; private set; }
    public double Target { get; private set; }
    public long RequestId { get; private set; }

    /// <summary>Desfecho da última busca (<see cref="MediaSeek"/>.Outcome*), ou null.</summary>
    public string? Outcome { get; private set; }

    /// <summary>
    /// Começa uma busca. Null se é o mesmo ponto de uma busca ainda em andamento (soltar a barra duas vezes no mesmo lugar,
    /// por exemplo): nada vai para a rede.
    /// </summary>
    public long? Begin(double target, long nowMs)
    {
        if (Phase == SeekPhase.Pending && Math.Abs(target - Target) < 0.5 && nowMs - _startedMs < MediaSeek.TimeoutMs) return null;
        Phase = SeekPhase.Pending;
        Target = target;
        Outcome = null;
        RequestId = ++_nextId;
        _startedMs = _phaseAtMs = nowMs;
        return RequestId;
    }

    /// <summary>A resposta do host. Respostas de pedidos antigos são ignoradas.</summary>
    public bool Resolve(MediaCommandResultMessage result, long nowMs)
    {
        if (Phase != SeekPhase.Pending || result.RequestId != RequestId) return false;
        Finish(result.Outcome, nowMs);
        return true;
    }

    /// <summary>Com um host sem resposta (1.3.0 ou mais antigo): a posição que chegou perto do pedido confirma a busca.</summary>
    public bool Observe(double position, long nowMs)
    {
        if (Phase != SeekPhase.Pending || !MediaSeek.Confirms(Target, position)) return false;
        Finish(MediaSeek.OutcomeDone, nowMs);
        return true;
    }

    /// <summary>O pedido nem saiu (conexão caiu, controles desligados).</summary>
    public void Fail(string outcome, long nowMs)
    {
        if (Phase == SeekPhase.Pending) Finish(outcome, nowMs);
    }

    /// <summary>Passa o tempo: sem resposta, falha; o aviso de pronto ou de falha some depois de uns segundos. true se mudou.</summary>
    public bool Tick(long nowMs)
    {
        if (Phase == SeekPhase.Pending && nowMs - _startedMs >= MediaSeek.TimeoutMs)
        {
            Finish(MediaSeek.OutcomeUnconfirmed, nowMs);
            return true;
        }
        if (Phase is SeekPhase.Done or SeekPhase.Failed && nowMs - _phaseAtMs >= 4000)
        {
            Phase = SeekPhase.Idle;
            return true;
        }
        return false;
    }

    public void Reset()
    {
        Phase = SeekPhase.Idle;
        Outcome = null;
    }

    private void Finish(string outcome, long nowMs)
    {
        Outcome = outcome;
        Phase = MediaSeek.Succeeded(outcome) ? SeekPhase.Done : SeekPhase.Failed;
        _phaseAtMs = nowMs;
    }
}

/// <summary>Lembra os últimos pedidos de cada pessoa que assiste: o mesmo número duas vezes é executado uma só.</summary>
public sealed class RequestDeduper(int capacity = 32)
{
    private readonly Queue<long> _order = new();
    private readonly HashSet<long> _seen = [];
    private readonly object _gate = new();

    /// <summary>true na primeira vez que o número aparece. 0 (viewers antigos, sem número) é sempre novo.</summary>
    public bool IsNew(long requestId)
    {
        if (requestId == 0) return true;
        lock (_gate)
        {
            if (!_seen.Add(requestId)) return false;
            _order.Enqueue(requestId);
            while (_order.Count > capacity) _seen.Remove(_order.Dequeue());
            return true;
        }
    }
}
