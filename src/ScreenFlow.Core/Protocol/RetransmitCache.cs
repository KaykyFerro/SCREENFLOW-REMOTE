namespace ScreenFlow.Core.Protocol;

/// <summary>Guarda os pacotes dos últimos frames para atender NACKs de retransmissão seletiva.</summary>
public sealed class RetransmitCache
{
    private readonly int _capacityFrames;
    private readonly Dictionary<uint, List<MediaChunk>> _frames = new();
    private readonly Queue<uint> _order = new();
    private readonly object _gate = new();

    public RetransmitCache(int capacityFrames = 64) => _capacityFrames = capacityFrames;

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
                var chunk = chunks.FirstOrDefault(c => c.Header.Type == MediaPacketType.Video && c.Header.Index == index);
                if (chunk is not null) result.Add(chunk);
            }
        }
        return result;
    }
}
