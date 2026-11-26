namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Ritmo de envio (pacing) do host. Mandar todos os pacotes de um frame de uma vez enche a fila do adaptador do Radmin,
/// do roteador ou do Wi-Fi, e o que sobra é descartado em sequência: várias perdas seguidas, que o FEC não recupera.
/// Com o ritmo, os pacotes saem espalhados num fluxo constante, bem acima do bitrate médio (o frame não atrasa), mas
/// sem rajadas.
/// </summary>
public static class SendPacing
{
    /// <summary>Mínimo do ritmo, mesmo com bitrate baixo: um frame comum sai em poucos milissegundos.</summary>
    public const double FloorMbps = 30;

    /// <summary>Quantas vezes o bitrate médio (de todo mundo somado) o ritmo deixa sair.</summary>
    public const double Headroom = 3;

    /// <summary>
    /// Nenhum pacote de vídeo espera mais que isso na fila: um keyframe grande (ou um frame para 10 pessoas) sai mais
    /// rápido que o ritmo normal, mas termina nesse tempo.
    /// </summary>
    public static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Retransmissões têm pressa (o viewer está esperando): saem em até 10 ms.</summary>
    public static readonly TimeSpan MaxUrgentDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Ritmo em bytes por segundo para <paramref name="averageMbps"/> de mídia (somando todos os viewers e a câmera).
    /// Com o upload conhecido (<paramref name="uploadMbps"/>), não passa de 90% dele, mas nunca fica abaixo de 1,5 vez a
    /// média (abaixo disso a fila só cresceria).
    /// </summary>
    public static double RateBytesPerSecond(double averageMbps, double uploadMbps = 0)
    {
        var mbps = Math.Max(averageMbps * Headroom, FloorMbps);
        if (uploadMbps > 0) mbps = Math.Min(mbps, Math.Max(uploadMbps * 0.9, averageMbps * 1.5));
        return Math.Max(mbps, 1) * 1_000_000 / 8;
    }
}

/// <summary>
/// Balde de fichas do ritmo de envio. Pode mandar enquanto o saldo é positivo (um pacote pode deixar o saldo negativo);
/// o saldo enche no ritmo e guarda no máximo <see cref="BurstBytes"/>, então o maior pedaço que sai junto é pequeno.
/// </summary>
public sealed class PacingBudget
{
    private double _tokens;
    private TimeSpan _last;

    public PacingBudget(double bytesPerSecond, TimeSpan now)
    {
        BytesPerSecond = bytesPerSecond;
        _last = now;
        _tokens = BurstBytes;
    }

    public double BytesPerSecond { get; set; }

    /// <summary>O que pode sair de uma vez: 2 ms do ritmo, e pelo menos 4 pacotes.</summary>
    public double BurstBytes => Math.Max(4 * 1_200, BytesPerSecond * 0.002);

    public bool CanSend => _tokens > 0;

    /// <summary>Enche o saldo pelo tempo que passou, no ritmo <paramref name="rate"/> (o normal ou mais rápido).</summary>
    public void Refill(TimeSpan now, double rate)
    {
        var elapsed = (now - _last).TotalSeconds;
        _last = now;
        if (elapsed <= 0) return;
        _tokens = Math.Min(Math.Max(BurstBytes, rate * 0.002), _tokens + elapsed * rate);
    }

    public void Consume(int bytes) => _tokens -= bytes;

    /// <summary>Quanto esperar até poder mandar de novo no ritmo <paramref name="rate"/> (zero se já pode).</summary>
    public TimeSpan Wait(double rate) => _tokens > 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((1 - _tokens) / Math.Max(1, rate));
}

/// <summary>
/// Fila do ritmo de envio: retransmissões na frente e os frames atrás, em ordem. Cada frame entra como uma rajada com
/// prazo (<see cref="SendPacing.MaxQueueDelay"/>); se no ritmo normal algum frame passaria do prazo, a fila acelera só o
/// necessário para cumprir. Assim o ritmo tira as rajadas sem virar atraso.
/// </summary>
public sealed class PacedQueue<T>
{
    private readonly Queue<(T Item, int Bytes)> _urgent = new();
    private readonly Queue<(T Item, int Bytes)> _media = new();
    private readonly Queue<(long End, TimeSpan Deadline)> _bursts = new();
    private readonly PacingBudget _budget;
    private long _mediaEnqueued;
    private long _mediaSent;
    private long _urgentBytes;
    private TimeSpan _urgentDeadline;

    public PacedQueue(double bytesPerSecond, TimeSpan now) => _budget = new PacingBudget(bytesPerSecond, now);

    /// <summary>Ritmo normal, em bytes por segundo.</summary>
    public double BytesPerSecond
    {
        get => _budget.BytesPerSecond;
        set => _budget.BytesPerSecond = value;
    }

    public long QueuedBytes => _urgentBytes + _mediaEnqueued - _mediaSent;

    public int Count => _urgent.Count + _media.Count;

    /// <summary>Um frame (os pacotes dele para todos os viewers), que precisa sair até <c>now + MaxQueueDelay</c>.</summary>
    public void EnqueueBurst(IEnumerable<(T Item, int Bytes)> items, TimeSpan now)
    {
        var before = _mediaEnqueued;
        foreach (var (item, bytes) in items)
        {
            _media.Enqueue((item, bytes));
            _mediaEnqueued += bytes;
        }
        if (_mediaEnqueued > before) _bursts.Enqueue((_mediaEnqueued, now + SendPacing.MaxQueueDelay));
    }

    public void EnqueueUrgent(T item, int bytes, TimeSpan now)
    {
        if (_urgent.Count == 0) _urgentDeadline = now + SendPacing.MaxUrgentDelay;
        _urgent.Enqueue((item, bytes));
        _urgentBytes += bytes;
    }

    /// <summary>Descarta os frames mais antigos até a fila caber em <paramref name="maxBytes"/> (o envio travou).</summary>
    public int TrimTo(long maxBytes)
    {
        var dropped = 0;
        while (QueuedBytes > maxBytes && _media.TryDequeue(out var old))
        {
            _mediaSent += old.Bytes;
            dropped++;
        }
        ForgetSentBursts();
        return dropped;
    }

    /// <summary>
    /// O ritmo de agora: o normal, ou mais rápido se algum frame da fila (ou a retransmissão) não cumpriria o prazo.
    /// </summary>
    public double RequiredRate(TimeSpan now)
    {
        var rate = _budget.BytesPerSecond;
        if (_urgentBytes > 0) rate = Math.Max(rate, _urgentBytes / Remaining(_urgentDeadline, now));
        foreach (var (end, deadline) in _bursts)
            rate = Math.Max(rate, (_urgentBytes + end - _mediaSent) / Remaining(deadline, now));
        return rate;
    }

    /// <summary>Tira da fila o que pode sair agora.</summary>
    public void Take(TimeSpan now, List<T> batch)
    {
        if (Count == 0)
        {
            _budget.Refill(now, _budget.BytesPerSecond);
            return;
        }
        var rate = RequiredRate(now);
        _budget.Refill(now, rate);
        while (_budget.CanSend)
        {
            if (_urgent.TryDequeue(out var urgent))
            {
                _urgentBytes -= urgent.Bytes;
                _budget.Consume(urgent.Bytes);
                batch.Add(urgent.Item);
            }
            else if (_media.TryDequeue(out var media))
            {
                _mediaSent += media.Bytes;
                _budget.Consume(media.Bytes);
                batch.Add(media.Item);
            }
            else break;
        }
        ForgetSentBursts();
    }

    /// <summary>Quanto esperar até o próximo envio (infinito com a fila vazia).</summary>
    public TimeSpan Wait(TimeSpan now) => Count == 0 ? Timeout.InfiniteTimeSpan : _budget.Wait(RequiredRate(now));

    private void ForgetSentBursts()
    {
        while (_bursts.TryPeek(out var burst) && burst.End <= _mediaSent) _bursts.Dequeue();
    }

    private static double Remaining(TimeSpan deadline, TimeSpan now) => Math.Max(0.001, (deadline - now).TotalSeconds);
}
