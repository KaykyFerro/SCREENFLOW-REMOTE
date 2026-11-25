namespace ScreenFlow.Core.Protocol;

/// <summary>Guarda os pacotes dos últimos frames para atender NACKs de retransmissão seletiva.</summary>
public sealed class RetransmitCache
{
    private readonly int _capacityFrames;
    private readonly MediaPacketType _dataType;
    private readonly Dictionary<uint, List<MediaChunk>> _frames = new();
    private readonly Queue<uint> _order = new();
    private readonly object _gate = new();

    /// <param name="dataType">Tipo dos pacotes de dados desta faixa (a tela ou a câmera que vai junto).</param>
    public RetransmitCache(int capacityFrames = 64, MediaPacketType dataType = MediaPacketType.Video)
    {
        _capacityFrames = capacityFrames;
        _dataType = dataType;
    }

    public void Store(uint frameId, List<MediaChunk> chunks)
    {
        lock (_gate)
        {
            if (_frames.TryAdd(frameId, chunks)) _order.Enqueue(frameId);
            while (_order.Count > _capacityFrames) _frames.Remove(_order.Dequeue());
        }
    }

    public List<MediaChunk> Lookup(uint frameId, IEnumerable<ushort> indices)
    {
        var result = new List<MediaChunk>();
        lock (_gate)
        {
            if (!_frames.TryGetValue(frameId, out var chunks)) return result;
            foreach (var index in indices)
            {
                var chunk = chunks.FirstOrDefault(c => c.Header.Type == _dataType && c.Header.Index == index);
                if (chunk is not null) result.Add(chunk);
            }
        }
        return result;
    }
}
