using System.Buffers.Binary;

namespace ScreenFlow.Core.Protocol;

public sealed record AssembledFrame(uint FrameId, byte[] Data, bool Keyframe, long TimestampUs, bool AfterGap);

public sealed record NackRequest(uint FrameId, ushort[] Missing);

/// <summary>
/// Remonta frames de vídeo a partir dos pacotes UDP, recupera perdas com FEC, pede retransmissão (NACK)
/// e descarta frames que não completam a tempo. Entrega sempre em ordem de frameId.
/// </summary>
public sealed class FrameReassembler
{
    private readonly SortedDictionary<uint, Assembly> _frames = new();
    private readonly TimeSpan _nackDelay;
    private readonly TimeSpan _deadline;
    private readonly MediaPacketType _dataType;
    private readonly MediaPacketType _fecType;
    private uint _nextFrame;
    private bool _started;
    private bool _gapPending;

    /// <param name="dataType">Faixa remontada: <see cref="MediaPacketType.Video"/> (a tela) ou <see cref="MediaPacketType.CameraVideo"/>
    /// (a câmera que vai junto). Pacotes da outra faixa são ignorados.</param>
    public FrameReassembler(TimeSpan? nackDelay = null, TimeSpan? deadline = null, MediaPacketType dataType = MediaPacketType.Video)
    {
        _nackDelay = nackDelay ?? TimeSpan.FromMilliseconds(8);
        _deadline = deadline ?? TimeSpan.FromMilliseconds(120);
        _dataType = dataType;
        _fecType = VideoTrack.FecType(dataType) ?? throw new ArgumentException("Só faixas de vídeo são remontadas.", nameof(dataType));
    }

    public long FramesCompleted { get; private set; }
    public long FramesDropped { get; private set; }
    public long PacketsRecoveredByFec { get; private set; }

    /// <summary>Adiciona um pacote de vídeo ou FEC da faixa. Retorna os frames que ficaram prontos, em ordem.</summary>
    public List<AssembledFrame> Add(in MediaHeader header, ReadOnlySpan<byte> payload, TimeSpan now)
    {
        var ready = new List<AssembledFrame>();
        if (header.Type != _dataType && header.Type != _fecType) return ready;
        if (!_started)
        {
            _started = true;
            _nextFrame = header.FrameId;
        }
        if (IsOlder(header.FrameId, _nextFrame)) return ready; // atrasado demais, já foi entregue ou descartado
        if (header.Count == 0) return ready;

        if (!_frames.TryGetValue(header.FrameId, out var frame))
        {
            if (_frames.Count > 512) return ready; // proteção de memória
            frame = new Assembly(header, now);
            _frames[header.FrameId] = frame;
        }

        if (header.Type == _dataType) frame.AddData(header.Index, payload);
        else frame.AddParity(header.Index, payload);

        PacketsRecoveredByFec += frame.TryRecover();
        Drain(ready, now);
        return ready;
    }

    /// <summary>Chamado periodicamente: gera NACKs para buracos e descarta frames vencidos.</summary>
    public (List<NackRequest> Nacks, List<AssembledFrame> Ready, int Dropped) Tick(TimeSpan now)
    {
        var nacks = new List<NackRequest>();
        var ready = new List<AssembledFrame>();
        var dropped = 0;
        foreach (var (id, frame) in _frames)
        {
            if (frame.Complete) continue;
            if (!frame.Nacked && now - frame.FirstSeen >= _nackDelay)
            {
                var missing = frame.MissingIndices();
                if (missing.Length > 0 && missing.Length <= 512) nacks.Add(new NackRequest(id, missing));
                frame.Nacked = true;
            }
        }

        // Descarta o frame esperado se venceu, ou se um frame mais novo já está completo há muito tempo.
        while (_frames.TryGetValue(_nextFrame, out var head) && !head.Complete && now - head.FirstSeen > _deadline)
        {
            _frames.Remove(_nextFrame);
            _nextFrame++;
            FramesDropped++;
            dropped++;
            _gapPending = true;
        }
        if (!_frames.ContainsKey(_nextFrame) && _frames.Count > 0)
        {
            var first = _frames.First();
            if (now - first.Value.FirstSeen > _deadline / 2)
            {
                var skipped = (int)(first.Key - _nextFrame);
                FramesDropped += skipped;
                dropped += skipped;
                _nextFrame = first.Key;
                _gapPending = true;
            }
        }
        Drain(ready, now);
        return (nacks, ready, dropped);
    }

    private void Drain(List<AssembledFrame> ready, TimeSpan now)
    {
        while (_frames.TryGetValue(_nextFrame, out var frame) && frame.Complete)
        {
            _frames.Remove(_nextFrame);
            ready.Add(new AssembledFrame(_nextFrame, frame.Build(), frame.Keyframe, frame.TimestampUs, _gapPending));
            _gapPending = false;
            FramesCompleted++;
            _nextFrame++;
        }
    }

    private static bool IsOlder(uint a, uint b) => (int)(a - b) < 0;

    private sealed class Assembly
    {
        private readonly byte[]?[] _data;
        private readonly byte[]?[] _parity;
        private readonly int _groupSize;
        private int _received;

        public Assembly(in MediaHeader header, TimeSpan now)
        {
            _data = new byte[header.Count][];
            _groupSize = Math.Max(1, (int)header.FecGroupSize);
            _parity = new byte[(header.Count + _groupSize - 1) / _groupSize][];
            Keyframe = header.Flags.HasFlag(MediaPacketFlags.Keyframe);
            TimestampUs = header.TimestampUs;
            FirstSeen = now;
        }

        public bool Keyframe { get; }
        public long TimestampUs { get; }
        public TimeSpan FirstSeen { get; }
        public bool Nacked { get; set; }
        public bool Complete => _received == _data.Length;

        public void AddData(int index, ReadOnlySpan<byte> payload)
        {
            if ((uint)index >= _data.Length || _data[index] is not null) return;
            _data[index] = payload.ToArray();
            _received++;
        }

        public void AddParity(int group, ReadOnlySpan<byte> payload)
        {
            if ((uint)group >= _parity.Length || _parity[group] is not null || payload.Length < 2) return;
            _parity[group] = payload.ToArray();
        }

        public int TryRecover()
        {
            if (Complete) return 0;
            var recovered = 0;
            for (var g = 0; g < _parity.Length; g++)
            {
                var parity = _parity[g];
                if (parity is null) continue;
                var start = g * _groupSize;
                var end = Math.Min(_data.Length, start + _groupSize);
                var missing = -1;
                var missingCount = 0;
                for (var i = start; i < end; i++)
                    if (_data[i] is null) { missing = i; missingCount++; }
                if (missingCount != 1) continue;

                var buffer = parity.AsSpan(2).ToArray();
                var length = BinaryPrimitives.ReadUInt16BigEndian(parity);
                for (var i = start; i < end; i++)
                {
                    if (i == missing) continue;
                    Packetizer.Xor(buffer, _data[i]);
                    length ^= (ushort)_data[i]!.Length;
                }
                if (length > buffer.Length) continue;
                _data[missing] = buffer[..length];
                _received++;
                recovered++;
            }
            return recovered;
        }

        public ushort[] MissingIndices()
        {
            var list = new List<ushort>();
            for (var i = 0; i < _data.Length; i++) if (_data[i] is null) list.Add((ushort)i);
            return list.ToArray();
        }

        public byte[] Build()
        {
            var total = 0;
            foreach (var part in _data) total += part!.Length;
            var result = new byte[total];
            var offset = 0;
            foreach (var part in _data)
            {
                part!.CopyTo(result, offset);
                offset += part.Length;
            }
            return result;
        }
    }
}
