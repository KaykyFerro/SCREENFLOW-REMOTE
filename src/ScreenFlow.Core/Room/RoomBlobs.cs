namespace ScreenFlow.Core.Room;

/// <summary>Um pedido de arquivo para mandar agora: de quem pedir e quais pedaços (vazio = todos).</summary>
public readonly record struct BlobAsk(string Id, Guid Source, int[] Missing);

/// <summary>
/// Downloads de arquivos da sala (foto do perfil e imagens do chat) pelo UDP da sala. Pede tudo de uma vez a quem tem o
/// arquivo; quando os pedaços param de chegar, pede só os que faltaram, algumas vezes; se não vier, tenta a próxima
/// pessoa que tem (o autor da mensagem e quem repassou o histórico, por exemplo). No fim confere o hash.
/// </summary>
public sealed class RoomBlobDownloads
{
    public const int MaxActive = 8;
    public const int MaxAttemptsPerSource = 5;
    /// <summary>Sem pedaço novo por este tempo: pede de novo o que falta.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, Download> _active = [];
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _active.Count; }
    }

    public bool IsActive(string id)
    {
        lock (_gate) return _active.ContainsKey(id);
    }

    /// <summary>Começa a baixar (o primeiro pedido sai no próximo <see cref="Due"/>). false se já está baixando ou não cabe.</summary>
    public bool Start(string id, int size, IEnumerable<Guid> sources, DateTimeOffset now)
    {
        var list = sources.Where(s => s != Guid.Empty).Distinct().ToList();
        if (!RoomBlob.IsValidId(id) || size is <= 0 or > RoomBlob.MaxImageBytes || list.Count == 0) return false;
        lock (_gate)
        {
            if (_active.TryGetValue(id, out var existing))
            {
                foreach (var source in list)
                    if (!existing.Sources.Contains(source)) existing.Sources.Add(source);
                return false;
            }
            if (_active.Count >= MaxActive) return false;
            _active[id] = new Download(id, size, list, now);
            return true;
        }
    }

    /// <summary>
    /// Um pedaço chegou. Devolve o arquivo quando ele fica completo e o hash confere; null enquanto falta algo (ou se o
    /// pedaço não é de um download ativo, de quem foi pedido).
    /// </summary>
    public byte[]? Accept(string id, Guid from, int size, int index, ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var download) || download.Size != size || download.CurrentSource != from) return null;
            if (index < 0 || index >= download.Received.Length || download.Received[index]) return null;
            if (data.Length != RoomBlob.ChunkLength(size, index)) return null;
            data.CopyTo(download.Data.AsSpan(index * RoomBlob.ChunkBytes));
            download.Received[index] = true;
            download.ReceivedCount++;
            download.Attempts = 0;
            download.NextRequest = now + Quiet;
            if (download.ReceivedCount < download.Received.Length) return null;

            if (RoomBlob.IdOf(download.Data) == id)
            {
                _active.Remove(id);
                return download.Data;
            }
            // Conteúdo errado (alguém mandou lixo): recomeça com a próxima pessoa.
            Array.Clear(download.Received);
            download.ReceivedCount = 0;
            download.NextSource(now);
            if (download.Exhausted) _active.Remove(id);
            return null;
        }
    }

    /// <summary>Pedidos para mandar agora e os downloads que desistiram (ninguém mandou o arquivo).</summary>
    public (List<BlobAsk> Asks, List<string> Failed) Due(DateTimeOffset now)
    {
        var asks = new List<BlobAsk>();
        var failed = new List<string>();
        lock (_gate)
        {
            foreach (var download in _active.Values)
            {
                if (now - download.Started > GiveUpAfter)
                {
                    failed.Add(download.Id);
                    continue;
                }
                if (now < download.NextRequest) continue;
                if (download.Attempts >= MaxAttemptsPerSource) download.NextSource(now);
                if (download.Exhausted)
                {
                    failed.Add(download.Id);
                    continue;
                }
                download.Attempts++;
                // Espera um pouco mais a cada tentativa sem resposta.
                download.NextRequest = now + Quiet * download.Attempts;
                asks.Add(new BlobAsk(download.Id, download.CurrentSource, download.Missing()));
            }
            foreach (var id in failed) _active.Remove(id);
        }
        return (asks, failed);
    }

    /// <summary>Quem sumiu da sala não vai mandar: passa para a próxima pessoa na hora.</summary>
    public void Forget(Guid source, DateTimeOffset now)
    {
        lock (_gate)
            foreach (var download in _active.Values)
                if (download.CurrentSource == source)
                {
                    download.NextSource(now);
                    download.NextRequest = now;
                }
    }

    private sealed class Download(string id, int size, List<Guid> sources, DateTimeOffset now)
    {
        private int _source;

        public string Id { get; } = id;
        public int Size { get; } = size;
        public List<Guid> Sources { get; } = sources;
        public byte[] Data { get; } = new byte[size];
        public bool[] Received { get; } = new bool[RoomBlob.ChunkCount(size)];
        public int ReceivedCount { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset Started { get; } = now;
        public DateTimeOffset NextRequest { get; set; } = now;
        public bool Exhausted => _source >= Sources.Count;
        public Guid CurrentSource => Exhausted ? Guid.Empty : Sources[_source];

        public void NextSource(DateTimeOffset now)
        {
            _source++;
            Attempts = 0;
            NextRequest = now;
        }

        /// <summary>Os pedaços que faltam (vazio quando não chegou nenhum: pede tudo).</summary>
        public int[] Missing()
        {
            if (ReceivedCount == 0) return [];
            var missing = new List<int>();
            for (var i = 0; i < Received.Length && missing.Count < RoomBlob.MaxMissingPerRequest; i++)
                if (!Received[i]) missing.Add(i);
            return [.. missing];
        }
    }
}
