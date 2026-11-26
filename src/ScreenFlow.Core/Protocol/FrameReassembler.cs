using System.Buffers.Binary;

namespace ScreenFlow.Core.Protocol;

public sealed record AssembledFrame(uint FrameId, byte[] Data, bool Keyframe, long TimestampUs, bool AfterGap);

/// <summary>Pedido de retransmissão. <see cref="Missing"/> vazio pede o frame inteiro (1.1.1: o frame sumiu todo).</summary>
public sealed record NackRequest(uint FrameId, ushort[] Missing);

/// <summary>
/// Remonta frames de vídeo a partir dos pacotes UDP, recupera perdas com FEC, pede retransmissão (NACK)
/// e descarta frames que não completam a tempo. Entrega sempre em ordem de frameId.
/// </summary>
/// <remarks>
/// O NACK sai quando o frame fica parado (nenhum pacote dele por <c>nackDelay</c>): com o ritmo de envio do host um frame
/// grande chega aos poucos, e pedir antes disso pediria pacotes que ainda estão a caminho. Se a retransmissão também se
/// perde, o pedido se repete (até <see cref="MaxNacksPerFrame"/> vezes, espaçado pelo RTT) enquanto houver prazo.
/// </remarks>
public sealed class FrameReassembler
{
    private const int MaxMissingFramesPerTick = 8;

    private readonly SortedDictionary<uint, Assembly> _frames = new();
    private readonly Dictionary<uint, (TimeSpan Last, int Count)> _wholeFrameNacks = new();
    private readonly TimeSpan _nackDelay;
    private readonly MediaPacketType _dataType;
    private readonly MediaPacketType _fecType;
    private TimeSpan _deadline;
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

    /// <summary>Quanto um frame incompleto pode segurar os seguintes antes de ser descartado.</summary>
    public TimeSpan Deadline
    {
        get => _deadline;
        set => _deadline = TimeSpan.FromTicks(Math.Clamp(value.Ticks, TimeSpan.FromMilliseconds(40).Ticks, TimeSpan.FromSeconds(1).Ticks));
    }

    /// <summary>Espera mínima entre dois NACKs do mesmo frame (o viewer ajusta pelo RTT).</summary>
    public TimeSpan NackRetryInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    public int MaxNacksPerFrame { get; set; } = 3;

    /// <summary>
    /// O host atende NACK de frame inteiro (1.1.1). Sem isso, um frame que sumiu todo é pulado na metade do prazo, como
    /// antes; com isso ele é pedido de novo e só é pulado no fim do prazo.
    /// </summary>
    public bool WholeFrameNack { get; set; }

    public long FramesCompleted { get; private set; }

    /// <summary>Todos os frames que não foram entregues: <see cref="FramesExpired"/> + <see cref="FramesMissing"/> + <see cref="FramesSuperseded"/>.</summary>
    public long FramesDropped => FramesExpired + FramesMissing + FramesSuperseded;

    /// <summary>Frames que chegaram em parte e venceram o prazo sem completar.</summary>
    public long FramesExpired { get; private set; }

    /// <summary>Frames dos quais nenhum pacote chegou.</summary>
    public long FramesMissing { get; private set; }

    /// <summary>Frames incompletos pulados porque um keyframe mais novo já estava completo.</summary>
    public long FramesSuperseded { get; private set; }

    public long PacketsRecoveredByFec { get; private set; }

    /// <summary>Buracos preenchidos por pacotes retransmitidos (depois de um NACK).</summary>
    public long PacketsRecoveredByNack { get; private set; }

    public long NacksSent { get; private set; }

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
            _wholeFrameNacks.Remove(header.FrameId);
        }
        frame.LastSeen = now;

        if (header.Type == _dataType)
        {
            if (frame.AddData(header.Index, payload) && header.Flags.HasFlag(MediaPacketFlags.Retransmit)) PacketsRecoveredByNack++;
        }
        else frame.AddParity(header.Index, payload, header.Flags.HasFlag(MediaPacketFlags.SpreadFec));

        PacketsRecoveredByFec += frame.TryRecover();
        Drain(ready);
        if (frame.Complete && frame.Keyframe && _frames.ContainsKey(header.FrameId))
        {
            SkipToKeyframe(header.FrameId);
            Drain(ready);
        }
        return ready;
    }

    /// <summary>
    /// Chamado periodicamente: gera NACKs para buracos e descarta frames vencidos. <paramref name="inputPending"/> diz que
    /// ainda há pacotes esperando para serem lidos (o PC ficou ocupado): o prazo dobra, porque os pacotes que faltam
    /// podem estar ali.
    /// </summary>
    public (List<NackRequest> Nacks, List<AssembledFrame> Ready, int Dropped) Tick(TimeSpan now, bool inputPending = false)
    {
        var nacks = new List<NackRequest>();
        var ready = new List<AssembledFrame>();
        var droppedBefore = FramesDropped;
        var deadline = inputPending ? _deadline * 2 : _deadline;

        uint? keyframe = null;
        foreach (var (id, frame) in _frames)
        {
            if (frame.Complete || frame.Nacks >= MaxNacksPerFrame) continue;
            if (now - frame.LastSeen < _nackDelay || now - frame.FirstSeen >= deadline) continue;
            if (frame.Nacks > 0 && now - frame.LastNack < NackRetryInterval) continue;
            // Parado: tudo que vinha já devia ter chegado, então o FEC pode usar qualquer paridade que chegou.
            frame.MarkAllDue();
            PacketsRecoveredByFec += frame.TryRecover();
            if (frame.Complete)
            {
                if (frame.Keyframe) keyframe = id;
                continue;
            }
            var missing = frame.MissingIndices();
            if (missing.Length is 0 or > 512) continue;
            nacks.Add(new NackRequest(id, missing));
            frame.Nacks++;
            frame.LastNack = now;
            NacksSent++;
        }

        if (keyframe is { } completed) SkipToKeyframe(completed);

        // Descarta o frame esperado se venceu.
        while (_frames.TryGetValue(_nextFrame, out var head) && !head.Complete && now - head.FirstSeen > deadline)
        {
            _frames.Remove(_nextFrame);
            _nextFrame++;
            FramesExpired++;
            _gapPending = true;
        }

        // O frame esperado não chegou nada, mas um mais novo já chegou: pede o frame inteiro e, no fim do prazo, pula.
        if (!_frames.ContainsKey(_nextFrame) && _frames.Count > 0)
        {
            var first = _frames.First();
            var waited = now - first.Value.FirstSeen;
            if (WholeFrameNack && waited >= _nackDelay) NackMissingFrames(first.Key, now, nacks);
            if (waited > (WholeFrameNack ? deadline : deadline / 2))
            {
                for (var id = _nextFrame; id != first.Key; id++) _wholeFrameNacks.Remove(id);
                FramesMissing += first.Key - _nextFrame;
                _nextFrame = first.Key;
                _gapPending = true;
            }
        }
        Drain(ready);
        return (nacks, ready, (int)(FramesDropped - droppedBefore));
    }

    private void NackMissingFrames(uint firstPresent, TimeSpan now, List<NackRequest> nacks)
    {
        var asked = 0;
        for (var id = _nextFrame; id != firstPresent && asked < MaxMissingFramesPerTick; id++, asked++)
        {
            _wholeFrameNacks.TryGetValue(id, out var state);
            if (state.Count >= MaxNacksPerFrame || (state.Count > 0 && now - state.Last < NackRetryInterval)) continue;
            nacks.Add(new NackRequest(id, []));
            _wholeFrameNacks[id] = (now, state.Count + 1);
            NacksSent++;
        }
    }

    /// <summary>
    /// Um keyframe completo recomeça a imagem: os frames presos antes dele (um incompleto e os que dependem dele) não
    /// precisam mais esperar o prazo.
    /// </summary>
    private void SkipToKeyframe(uint keyframeId)
    {
        if (keyframeId == _nextFrame || IsOlder(keyframeId, _nextFrame)) return;
        while (_nextFrame != keyframeId)
        {
            if (_frames.Remove(_nextFrame)) FramesSuperseded++;
            else FramesMissing++;
            _wholeFrameNacks.Remove(_nextFrame);
            _nextFrame++;
        }
        _gapPending = false; // o keyframe recomeça a imagem: não precisa pedir outro
    }

    private void Drain(List<AssembledFrame> ready)
    {
        while (_frames.TryGetValue(_nextFrame, out var frame) && frame.Complete)
        {
            _frames.Remove(_nextFrame);
            ready.Add(new AssembledFrame(_nextFrame, frame.Build(), frame.Keyframe, frame.TimestampUs, _gapPending && !frame.Keyframe));
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
        private bool _spread;
        private int _received;
        private int _due = -1;

        public Assembly(in MediaHeader header, TimeSpan now)
        {
            _data = new byte[header.Count][];
            _groupSize = Math.Max(1, (int)header.FecGroupSize);
            _parity = new byte[Packetizer.FecGroups(header.Count, _groupSize)][];
            Keyframe = header.Flags.HasFlag(MediaPacketFlags.Keyframe);
            TimestampUs = header.TimestampUs;
            FirstSeen = now;
            LastSeen = now;
        }

        public bool Keyframe { get; }
        public long TimestampUs { get; }
        public TimeSpan FirstSeen { get; }
        public TimeSpan LastSeen { get; set; }
        public TimeSpan LastNack { get; set; }
        public int Nacks { get; set; }
        public bool Complete => _received == _data.Length;

        /// <summary>Guarda um pacote de dados. Retorna true se ele preencheu um buraco (não era repetido).</summary>
        public bool AddData(int index, ReadOnlySpan<byte> payload)
        {
            if ((uint)index >= _data.Length) return false;
            _due = Math.Max(_due, index - 1); // os dados saem em ordem: os anteriores já foram enviados
            if (_data[index] is not null) return false;
            _data[index] = payload.ToArray();
            _received++;
            return true;
        }

        public void AddParity(int group, ReadOnlySpan<byte> payload, bool spread)
        {
            if ((uint)group >= _parity.Length || payload.Length < 2) return;
            // A paridade do grupo g sai logo depois do pacote de dados (g + 1)·N - 1.
            _due = Math.Max(_due, Math.Min((group + 1) * _groupSize, _data.Length) - 1);
            if (_parity[group] is not null) return;
            _parity[group] = payload.ToArray();
            _spread = spread;
        }

        /// <summary>O frame parou de chegar: qualquer pacote que falta já devia ter chegado.</summary>
        public void MarkAllDue() => _due = _data.Length - 1;

        public int TryRecover()
        {
            if (Complete) return 0;
            var recovered = 0;
            for (var g = 0; g < _parity.Length; g++)
            {
                var parity = _parity[g];
                if (parity is null) continue;
                var members = Packetizer.GroupMembers(g, _data.Length, _groupSize, _spread);
                var missing = -1;
                var missingCount = 0;
                foreach (var i in members)
                    if (_data[i] is null) { missing = i; missingCount++; }
                // Só recupera o que já devia ter chegado: a paridade espalhada chega antes de parte do grupo, e
                // "recuperar" um pacote que ainda está a caminho só gastaria CPU (e enganaria a estatística).
                if (missingCount != 1 || missing > _due) continue;

                var buffer = parity.AsSpan(2).ToArray();
                var length = BinaryPrimitives.ReadUInt16BigEndian(parity);
                foreach (var i in members)
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
