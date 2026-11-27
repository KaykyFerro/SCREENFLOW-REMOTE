using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>Os avisos sonoros do app fora da voz (1.5.0).</summary>
public enum AppSound
{
    /// <summary>Mensagem nova num canal de texto.</summary>
    Message,
    /// <summary>Mensagem nova numa conversa privada.</summary>
    DirectMessage,
    /// <summary>Alguém pedindo para assistir ou para entrar.</summary>
    Alert,
    /// <summary>Um envio de arquivo terminou.</summary>
    Done,
}

/// <summary>
/// (1.5.0) Os sons do app que não são a voz: avisos (mensagem nova, alguém pedindo para assistir) e trechos de áudio
/// (mensagens de voz, figurinhas com som). Tudo passa pelo volume "Sons do aplicativo" do mixer e sai pela saída
/// escolhida para a voz (o fone), ou pela padrão do Windows. A saída só abre quando um som toca e fecha sozinha depois
/// de alguns segundos em silêncio, então nada fica aberto à toa.
/// </summary>
public sealed class AppSoundPlayer : IDisposable
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(AudioProfile.SampleRate, AudioProfile.Channels);
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(8);

    // Depois dos campos acima, de propósito: os campos estáticos são preenchidos na ordem do arquivo, e o construtor
    // usa o Format (antes dele, o Format ainda era null e o ScreenFlow nem abria).
    public static AppSoundPlayer Shared { get; } = new();

    private readonly object _gate = new();
    private readonly MixingSampleProvider _mixer = new(Format) { ReadFully = true };
    private readonly System.Threading.Timer _idle;
    private WasapiOut? _output;
    private string? _openedOn;
    private long _lastSoundTick;
    private bool _disposed;

    public AppSoundPlayer() => _idle = new System.Threading.Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>A saída escolhida para a voz (null: a padrão do Windows). Vale no próximo som.</summary>
    public string? SpeakerId { get; set; }

    /// <summary>Toca um aviso curto.</summary>
    public void Play(AppSound sound) => Add(new ToneClip(sound));

    /// <summary>
    /// Toca um trecho de áudio (estéreo, 48 kHz, intercalado). Devolve o controle da reprodução (pausar, pular, parar,
    /// velocidade); ele termina sozinho no fim.
    /// </summary>
    public ClipPlayback PlayClip(float[] stereo, double speed = 1, TimeSpan? start = null)
    {
        var playback = new ClipPlayback(stereo, speed, start ?? TimeSpan.Zero);
        Add(playback);
        return playback;
    }

    private void Add(ISampleProvider provider)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                EnsureOpen();
                _mixer.AddMixerInput(provider);
                _lastSoundTick = Environment.TickCount64;
                _idle.Change(IdleClose, IdleClose);
            }
            catch (Exception exception)
            {
                Log.Warn($"Não deu para tocar o som do app: {exception.Message}");
                Close();
            }
        }
    }

    private void EnsureOpen()
    {
        if (_output is not null && _openedOn == SpeakerId) return;
        Close();
        var output = OpenDevice(SpeakerId);
        output.Init(new GainSampleProvider(_mixer, AudioMixer.AppSounds));
        output.Play();
        _output = output;
        _openedOn = SpeakerId;
    }

    private static WasapiOut OpenDevice(string? speakerId)
    {
        if (speakerId is { } id)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            }
            catch (Exception exception) { Log.Debug($"Saída escolhida indisponível para os sons do app: {exception.Message}"); }
        }
        return new WasapiOut(AudioClientShareMode.Shared, true, 60);
    }

    private void CloseIfIdle()
    {
        lock (_gate)
        {
            if (_output is null) return;
            if (_mixer.MixerInputs.Any() || Environment.TickCount64 - _lastSoundTick < IdleClose.TotalMilliseconds) return;
            Close();
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Close()
    {
        var output = _output;
        _output = null;
        _openedOn = null;
        if (output is null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idle.Dispose();
            _mixer.RemoveAllMixerInputs();
            Close();
        }
    }

    /// <summary>Um aviso de poucas notas suaves (seno com um harmônico, ataque e saída em rampa).</summary>
    private sealed class ToneClip : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ToneClip(AppSound sound)
        {
            (double Hz, int Ms)[] notes = sound switch
            {
                AppSound.Message => [(783.99, 55), (1174.66, 110)],
                AppSound.DirectMessage => [(659.25, 70), (987.77, 70), (1318.51, 130)],
                AppSound.Alert => [(523.25, 110), (659.25, 110), (783.99, 180)],
                _ => [(1046.50, 70), (1567.98, 140)],
            };
            const double amplitude = 0.14;
            var total = notes.Sum(n => n.Ms) * AudioProfile.SampleRate / 1000;
            _samples = new float[total * 2];
            var offset = 0;
            foreach (var (hz, ms) in notes)
            {
                var count = ms * AudioProfile.SampleRate / 1000;
                for (var i = 0; i < count; i++)
                {
                    var t = i / (double)AudioProfile.SampleRate;
                    var attack = Math.Min(1, i / (AudioProfile.SampleRate * 0.005));
                    var release = Math.Min(1, (count - i) / (AudioProfile.SampleRate * 0.04));
                    var tone = Math.Sin(2 * Math.PI * hz * t) + 0.2 * Math.Sin(4 * Math.PI * hz * t);
                    var value = (float)(tone * amplitude * attack * release);
                    _samples[(offset + i) * 2] = value;
                    _samples[(offset + i) * 2 + 1] = value;
                }
                offset += count;
            }
        }

        public WaveFormat WaveFormat => Format;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _samples.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}

/// <summary>
/// (1.5.0) Um trecho de áudio tocando nos sons do app (mensagem de voz, figurinha com som): pausar, continuar, pular
/// para um ponto, mudar a velocidade (mantendo o tom da voz, <see cref="TimeStretcher"/>) e parar. Seguro para chamar
/// de qualquer thread; <see cref="Ended"/> é chamado na thread do áudio.
/// </summary>
public sealed class ClipPlayback : ISampleProvider
{
    private const int Channels = AudioProfile.Channels;

    private readonly object _gate = new();
    private readonly float[] _stereo;
    private readonly TimeStretcher _stretcher = new(AudioProfile.SampleRate, Channels);
    private int _position; // em quadros (amostra por canal)
    private double _speed;
    private bool _paused;
    private bool _stopped;
    private bool _endedRaised;
    private float _fade = 1f;

    internal ClipPlayback(float[] stereo, double speed, TimeSpan start)
    {
        _stereo = stereo;
        _speed = TimeStretcher.CleanSpeed(speed);
        _position = Math.Clamp((int)(start.TotalSeconds * AudioProfile.SampleRate), 0, Frames);
    }

    private int Frames => _stereo.Length / Channels;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioProfile.SampleRate, Channels);

    public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)AudioProfile.SampleRate);

    public TimeSpan Position
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_position / (double)AudioProfile.SampleRate); }
    }

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set { lock (_gate) _paused = value; }
    }

    public bool IsFinished
    {
        get { lock (_gate) return _stopped || _position >= Frames; }
    }

    /// <summary>Velocidade (0,5 a 2), sem mudar o tom.</summary>
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            lock (_gate)
            {
                _speed = TimeStretcher.CleanSpeed(value);
                _stretcher.Reset();
                _fade = 0f;
            }
        }
    }

    /// <summary>Terminou (chegou ao fim ou foi parado). Chamado uma vez.</summary>
    public event Action<ClipPlayback>? Ended;

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            _position = Math.Clamp((int)(position.TotalSeconds * AudioProfile.SampleRate), 0, Frames);
            _stretcher.Reset();
            _fade = 0f; // volta em rampa no ponto novo (sem estalo)
        }
    }

    public void Stop()
    {
        lock (_gate) _stopped = true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        bool ended;
        int produced;
        lock (_gate)
        {
            if (_stopped || _position >= Frames)
            {
                produced = 0;
            }
            else if (_paused)
            {
                Array.Clear(buffer, offset, count);
                return count; // pausado: silêncio, mas continua no mix
            }
            else
            {
                var output = buffer.AsSpan(offset, count - count % Channels);
                int written;
                if (Math.Abs(_speed - 1) < 0.001)
                {
                    var floats = Math.Min(output.Length, (Frames - _position) * Channels);
                    _stereo.AsSpan(_position * Channels, floats).CopyTo(output);
                    written = floats / Channels;
                    _position += written;
                }
                else written = _stretcher.Process(_stereo, ref _position, output, _speed);
                // Rampa curta depois de um pulo ou de mudar a velocidade.
                for (var f = 0; f < written && _fade < 1f; f++)
                {
                    _fade = Math.Min(1f, _fade + 1f / 240);
                    for (var c = 0; c < Channels; c++) output[f * Channels + c] *= _fade;
                }
                produced = written * Channels;
            }
            ended = produced == 0 && !_endedRaised;
            if (ended) _endedRaised = true;
        }
        if (ended) Ended?.Invoke(this);
        return produced;
    }
}
