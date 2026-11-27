using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Infrastructure.Media;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>
/// Voz de um PC na sala: microfone → ganho do microfone → portão de voz → ganho automático, compressor e limitador
/// (1.5.0, <see cref="VoiceProcessor"/>) → Opus; e, na volta, um decoder, um nivelador e um buffer por pessoa, todos
/// mixados numa saída só. Só manda áudio quando tem voz (e não está mudo); cada quadro leva o anterior junto.
/// </summary>
public sealed class VoiceEngine : IDisposable
{
    private readonly IVoiceInput _input;
    private readonly VoiceEncoder _encoder;
    private readonly VoiceOutput? _output;
    private readonly VoiceGate _gate = new();
    private readonly VoiceProcessor _processor = new(VoiceFormat.SampleRate);
    private readonly float[] _work = new float[VoiceFormat.FrameSamples];
    private volatile bool _leveler = true;
    private readonly Dictionary<Guid, RemoteVoice> _remotes = [];
    private readonly Dictionary<Guid, float> _peerVolumes = [];
    private readonly object _remotesGate = new();
    private readonly object _encodeGate = new();
    private byte[] _previous = [];
    private uint _sequence;
    private volatile bool _muted;
    private volatile bool _deafened;
    private bool _disposed;

    /// <summary>Apertar para falar (1.4.0): com ele ligado, a voz só vai enquanto a tecla está apertada.</summary>
    public PushToTalk PushToTalk { get; } = new();

    public VoiceEngine(IVoiceInput input, float volume, double sensitivity, bool outputEnabled = true, string? speakerId = null)
    {
        _input = input;
        _encoder = new VoiceEncoder();
        _gate.Sensitivity = sensitivity;
        if (outputEnabled)
        {
            try { _output = new VoiceOutput(volume, speakerId); }
            catch (Exception exception) { Log.Warn($"Saída da voz indisponível: {exception.Message}"); }
        }
    }

    /// <summary>Quadro pronto para enviar: número, volume (0–255), quadro atual e o anterior (vazio no começo da fala).</summary>
    public event Action<uint, byte, byte[], byte[]>? FrameEncoded;

    public string InputDescription => _input.Description;

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public bool Deafened
    {
        get => _deafened;
        set
        {
            _deafened = value;
            if (_output is not null) _output.Deafened = value;
        }
    }

    public float Volume
    {
        get => _output?.Volume ?? 1f;
        set { if (_output is not null) _output.Volume = value; }
    }

    public double Sensitivity
    {
        get => _gate.Sensitivity;
        set => _gate.Sensitivity = value;
    }

    /// <summary>(1.5.0) Volume do microfone em dB (−12 a +24), antes do portão de voz.</summary>
    public double MicGainDb
    {
        get => _processor.ManualGainDb;
        set { lock (_encodeGate) _processor.ManualGainDb = value; }
    }

    /// <summary>(1.5.0) Ajuste automático do volume do microfone.</summary>
    public bool AutoGain
    {
        get => _processor.AutoGain;
        set { lock (_encodeGate) _processor.AutoGain = value; }
    }

    /// <summary>(1.5.0) O ganho que o ajuste automático está usando agora (dB), para as configurações e o diagnóstico.</summary>
    public double AutoGainDb => _processor.AutoGainDb;

    /// <summary>(1.5.0) Iguala o volume de quem chega baixo (ou numa versão antiga) com o dos outros.</summary>
    public bool Leveler
    {
        get => _leveler;
        set
        {
            _leveler = value;
            lock (_remotesGate) foreach (var remote in _remotes.Values) remote.Leveler = value;
        }
    }

    /// <summary>Quantas vezes o limitador da saída precisou agir (muitas vozes altas juntas).</summary>
    public long OutputLimited => _output?.LimiterEngaged ?? 0;

    /// <summary>Você está falando agora (portão aberto e microfone ligado).</summary>
    public bool IsSpeaking => !_muted && PushToTalk.ShouldSend(_gate.IsOpen, Environment.TickCount64);

    /// <summary>Volume do microfone em dBFS (mesmo mudo, para o medidor das configurações).</summary>
    public double InputLevelDb => _gate.LevelDb;

    public long FramesSent { get; private set; }
    public long FramesDecoded
    {
        get { lock (_remotesGate) return _remotes.Values.Sum(r => r.FramesDecoded); }
    }
    public long FramesRecovered
    {
        get { lock (_remotesGate) return _remotes.Values.Sum(r => r.FramesRecovered); }
    }

    /// <summary>(1.5.0) O nível da fala mais alta que está chegando (dBFS), já com o nivelador. Null sem ninguém falando.</summary>
    public double? HeardLevelDb
    {
        get { lock (_remotesGate) return _remotes.Values.Max(r => r.LevelDb); }
    }

    public void Start()
    {
        _input.FrameReady += OnFrame;
        _input.Start();
    }

    private void OnFrame(float[] input)
    {
        lock (_encodeGate)
        {
            if (_disposed) return;
            // O quadro do microfone é do microfone (ele reaproveita o array): o tratamento mexe numa cópia.
            var count = Math.Min(input.Length, _work.Length);
            input.AsSpan(0, count).CopyTo(_work);
            var frame = _work.AsSpan(0, count);
            _processor.ApplyManualGain(frame);
            var open = PushToTalk.ShouldSend(_gate.Process(frame), Environment.TickCount64);
            if (_muted || !open)
            {
                _previous = [];
                return;
            }
            // Só quadros com voz de verdade (acima do limiar, não o "fim" do portão) ensinam o ganho automático.
            _processor.Process(frame, speech: _gate.LevelDb >= _gate.ThresholdDb);
            byte[]? data;
            try { data = _encoder.Encode(frame); }
            catch (Exception exception)
            {
                Log.Debug($"Quadro de voz descartado: {exception.Message}");
                return;
            }
            if (data is null || data.Length == 0) return;
            FrameEncoded?.Invoke(_sequence++, _gate.LevelByte, data, _previous);
            _previous = data;
            FramesSent++;
        }
    }

    /// <summary>Quadro de voz de outra pessoa. Recupera um quadro perdido com a cópia que vem no seguinte.</summary>
    public void Receive(Guid peer, uint sequence, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        if (_deafened) return;
        RemoteVoice remote;
        lock (_remotesGate)
        {
            if (_disposed) return;
            if (!_remotes.TryGetValue(peer, out remote!))
            {
                try { remote = new RemoteVoice(_output) { Volume = _peerVolumes.GetValueOrDefault(peer, 1f), Leveler = _leveler }; }
                catch (Exception exception)
                {
                    Log.Warn($"Não deu para tocar a voz de outra pessoa: {exception.Message}");
                    return;
                }
                _remotes[peer] = remote;
            }
        }
        remote.Receive(sequence, current, previous);
    }

    /// <summary>Volume de uma pessoa só (0 a 2), como o "Volume do usuário" do Discord.</summary>
    public void SetPeerVolume(Guid peer, float volume)
    {
        lock (_remotesGate)
        {
            _peerVolumes[peer] = volume;
            if (_remotes.TryGetValue(peer, out var remote)) remote.Volume = volume;
        }
    }

    public void RemovePeer(Guid peer)
    {
        RemoteVoice? remote;
        lock (_remotesGate) _remotes.Remove(peer, out remote);
        remote?.Dispose();
    }

    public void Play(VoiceCue cue) => _output?.Play(cue);

    public void Dispose()
    {
        _input.FrameReady -= OnFrame;
        _input.Dispose();
        lock (_encodeGate)
        {
            _disposed = true;
            _encoder.Dispose();
        }
        List<RemoteVoice> remotes;
        lock (_remotesGate)
        {
            remotes = [.. _remotes.Values];
            _remotes.Clear();
        }
        foreach (var remote in remotes) remote.Dispose();
        _output?.Dispose();
    }

    private sealed class RemoteVoice : IDisposable
    {
        private readonly object _gate = new();
        private readonly VoiceOutput? _output;
        private readonly VoiceDecoder _decoder = new();
        private readonly VoiceLeveler _leveler = new();
        private readonly JitterBuffer? _buffer;
        private uint? _last;
        private bool _disposed;

        public RemoteVoice(VoiceOutput? output)
        {
            _output = output;
            _buffer = output?.AddStream();
        }

        public long FramesDecoded { get; private set; }
        public long FramesRecovered { get; private set; }

        /// <summary>O nível médio da fala dessa pessoa como ela toca aqui (dBFS), depois do nivelador.</summary>
        public double? LevelDb { get; private set; }

        public float Volume
        {
            set { if (_buffer is not null) _buffer.Volume = value; }
        }

        public bool Leveler
        {
            set { lock (_gate) _leveler.Enabled = value; }
        }

        public void Receive(uint sequence, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_last is { } last)
                {
                    var gap = sequence - last;
                    if (gap == 0 || gap > int.MaxValue) return; // repetido ou fora de ordem
                    if (gap == 2 && !previous.IsEmpty)
                    {
                        Play(previous);
                        FramesRecovered++;
                    }
                }
                _last = sequence;
                Play(current);
            }
        }

        private void Play(ReadOnlySpan<byte> data)
        {
            var samples = _decoder.Decode(data);
            if (samples.IsEmpty) return;
            FramesDecoded++;
            _leveler.Process(samples);
            // O nível da fala que chega (para o autoteste e o diagnóstico): só quadros com som contam.
            var level = VoiceGate.RmsDb(samples);
            if (level > -50) LevelDb = LevelDb is { } current ? current * 0.9 + level * 0.1 : level;
            _buffer?.Write(samples);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_buffer is not null) _output?.RemoveStream(_buffer);
                _decoder.Dispose();
            }
        }
    }
}
