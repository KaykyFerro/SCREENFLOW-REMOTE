using System.Buffers.Binary;
using ScreenFlow.Core.Protocol;

namespace ScreenFlow.Core.Server;

/// <summary>Um pacote de vídeo guardado pelo servidor: o cabeçalho (sem número de pacote) e o conteúdo já aberto.</summary>
public readonly record struct RelayPacket(MediaHeader Header, byte[] Payload);

/// <summary>
/// O que o servidor tem de um frame pedido num NACK: os pacotes que achou (inclusive os que a paridade recuperou), os
/// que faltam e se ele não viu nada do frame (aí o pedido vai inteiro para quem transmite).
/// </summary>
public sealed record RelayLookup(List<RelayPacket> Found, List<ushort> Missing, bool Unknown);

/// <summary>
/// Os pacotes recentes de uma faixa de vídeo de uma transmissão, guardados no servidor (1.4.1). Com eles o servidor
/// atende o NACK de quem assiste na hora, sem pedir de novo a quem transmite, e não repassa duas vezes um pacote que
/// chegou repetido. Guarda também a paridade (FEC), então um pacote que nunca chegou ao servidor mas que a paridade
/// recupera também é atendido.
/// </summary>
public sealed class RelayTrackCache
{
    /// <summary>Frames muito grandes são ignorados (protege a memória do servidor).</summary>
    public const int MaxPacketsPerFrame = 8192;

    private readonly int _capacity;
    private readonly MediaPacketType _dataType;
    private readonly MediaPacketType _fecType;
    private readonly Dictionary<uint, Frame> _frames = new();
    private readonly Queue<uint> _order = new();
    private readonly object _gate = new();

    public RelayTrackCache(int capacityFrames, MediaPacketType dataType)
    {
        if (capacityFrames < 1) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _capacity = capacityFrames;
        _dataType = dataType;
        _fecType = VideoTrack.FecType(dataType) ?? throw new ArgumentException("Só faixas de vídeo são guardadas.", nameof(dataType));
    }

    public int Frames
    {
        get { lock (_gate) return _frames.Count; }
    }

    /// <summary>
    /// Guarda um pacote de dados ou de paridade desta faixa. Retorna false se ele já estava guardado (um repetido, que não
    /// precisa ser repassado de novo) ou se não é desta faixa.
    /// </summary>
    public bool Store(in MediaHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.Type != _dataType && header.Type != _fecType) return false;
        if (header.Count is 0 or > MaxPacketsPerFrame) return false;
        lock (_gate)
        {
            if (!_frames.TryGetValue(header.FrameId, out var frame))
            {
                frame = new Frame(header, _dataType);
                _frames[header.FrameId] = frame;
                _order.Enqueue(header.FrameId);
                while (_order.Count > _capacity) _frames.Remove(_order.Dequeue());
            }
            else if (frame.Count != header.Count) return false; // outro frame com o mesmo número (quem transmite recomeçou)
            return header.Type == _dataType ? frame.AddData(header.Index, payload) : frame.AddParity(header.Index, payload, header.Flags.HasFlag(MediaPacketFlags.SpreadFec));
        }
    }

    /// <summary>Tem o pacote de dados <paramref name="index"/> do frame (sem tentar recuperar).</summary>
    public bool Has(uint frameId, int index)
    {
        lock (_gate) return _frames.TryGetValue(frameId, out var frame) && frame.HasData(index);
    }

    /// <summary>
    /// Os pacotes de dados pedidos. Sem índices, o frame inteiro. O que falta e a paridade recupera é recuperado (e fica
    /// guardado). Se o servidor não viu nada do frame, <see cref="RelayLookup.Unknown"/> vem true.
    /// </summary>
    public RelayLookup Find(uint frameId, IReadOnlyCollection<ushort> indices)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(frameId, out var frame)) return new RelayLookup([], [.. indices], true);
            var wanted = indices.Count == 0 ? Enumerable.Range(0, frame.Count).Select(i => (ushort)i) : indices.Distinct();
            var found = new List<RelayPacket>();
            var missing = new List<ushort>();
            foreach (var index in wanted)
            {
                if (index >= frame.Count) continue;
                var data = frame.Data(index) ?? frame.Recover(index);
                if (data is not null) found.Add(new RelayPacket(frame.HeaderFor(index, data.Length), data));
                else missing.Add(index);
            }
            return new RelayLookup(found, missing, false);
        }
    }

    private sealed class Frame
    {
        private readonly byte[]?[] _data;
        private readonly byte[]?[] _parity;
        private readonly int _groupSize;
        private readonly MediaHeader _template;
        private bool _spread;

        public Frame(in MediaHeader header, MediaPacketType dataType)
        {
            Count = header.Count;
            _data = new byte[Count][];
            _groupSize = Math.Max(1, (int)header.FecGroupSize);
            _parity = header.FecGroupSize > 1 ? new byte[Packetizer.FecGroups(Count, _groupSize)][] : [];
            _template = new MediaHeader
            {
                Type = dataType,
                FrameId = header.FrameId,
                Count = header.Count,
                FecGroupSize = header.FecGroupSize,
                Flags = header.Flags & MediaPacketFlags.Keyframe,
                TimestampUs = header.TimestampUs,
            };
        }

        public int Count { get; }

        public bool HasData(int index) => (uint)index < _data.Length && _data[index] is not null;

        public byte[]? Data(int index) => (uint)index < _data.Length ? _data[index] : null;

        public bool AddData(int index, ReadOnlySpan<byte> payload)
        {
            if ((uint)index >= _data.Length || _data[index] is not null) return false;
            _data[index] = payload.ToArray();
            return true;
        }

        public bool AddParity(int group, ReadOnlySpan<byte> payload, bool spread)
        {
            if ((uint)group >= _parity.Length || payload.Length < 2 || _parity[group] is not null) return false;
            _parity[group] = payload.ToArray();
            _spread = spread;
            return true;
        }

        /// <summary>Recupera um pacote de dados pela paridade do grupo dele, se só ele falta no grupo.</summary>
        public byte[]? Recover(int index)
        {
            if (_parity.Length == 0) return null;
            var groups = _parity.Length;
            var group = _spread ? index % groups : index / _groupSize;
            if ((uint)group >= groups || _parity[group] is not { } parity) return null;
            var members = Packetizer.GroupMembers(group, Count, _groupSize, _spread);
            if (!members.Contains(index)) return null;
            var buffer = parity.AsSpan(2).ToArray();
            var length = BinaryPrimitives.ReadUInt16BigEndian(parity);
            foreach (var member in members)
            {
                if (member == index) continue;
                if (_data[member] is not { } other) return null; // falta mais de um no grupo
                Packetizer.Xor(buffer, other);
                length ^= (ushort)other.Length;
            }
            if (length > buffer.Length) return null;
            var recovered = buffer[..length];
            _data[index] = recovered;
            return recovered;
        }

        public MediaHeader HeaderFor(int index, int length)
        {
            var header = _template;
            header.Index = (ushort)index;
            header.PayloadLength = (ushort)length;
            return header;
        }
    }
}
