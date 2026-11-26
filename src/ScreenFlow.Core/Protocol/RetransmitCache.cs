namespace ScreenFlow.Core.Protocol;

/// <summary>Guarda os pacotes dos últimos frames para atender NACKs de retransmissão seletiva.</summary>
public sealed class RetransmitCache
{
    private readonly int _capacityFrames;
    private readonly MediaPacketType _dataType;
    private readonly Dictionary<uint, MediaChunk[]> _frames = new();
    private readonly Queue<uint> _order = new();
    private readonly object _gate = new();

    /// <param name="dataType">Tipo dos pacotes de dados desta faixa (a tela ou a câmera que vai junto).</param>
    public RetransmitCache(int capacityFrames = 64, MediaPacketType dataType = MediaPacketType.Video)
    {
        _capacityFrames = capacityFrames;
        _dataType = dataType;
    }

    /// <summary>Guarda os pacotes de dados de um frame (a paridade não é guardada: o NACK pede dados).</summary>
    public void Store(uint frameId, IEnumerable<MediaChunk> chunks)
    {
        var data = chunks.Where(c => c.Header.Type == _dataType).OrderBy(c => c.Header.Index).ToArray();
        lock (_gate)
        {
            if (_frames.TryAdd(frameId, data)) _order.Enqueue(frameId);
            while (_order.Count > _capacityFrames) _frames.Remove(_order.Dequeue());
        }
    }

    /// <summary>Os pacotes pedidos. Sem índices, o frame inteiro (o viewer não recebeu nada dele).</summary>
    public List<MediaChunk> Lookup(uint frameId, IEnumerable<ushort> indices)
    {
        var result = new List<MediaChunk>();
        lock (_gate)
        {
            if (!_frames.TryGetValue(frameId, out var chunks)) return result;
            var any = false;
            foreach (var index in indices)
            {
                any = true;
                if (index < chunks.Length && chunks[index].Header.Index == index) result.Add(chunks[index]);
            }
            if (!any) result.AddRange(chunks);
        }
        return result;
    }
}
