using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;

namespace ScreenFlow.Infrastructure.Media;

public readonly record struct AudioPacket(byte[] Data, long TimestampUs);

/// <summary>Origem de áudio do host: o som do PC (WASAPI) ou um tom de teste.</summary>
public interface IAudioSource : IDisposable
{
    AudioProfile Profile { get; }

    /// <summary>Nível e espectro do que está sendo capturado (alimenta o visualizador do host).</summary>
    AudioMeter Meter { get; }

    /// <summary>Pausado: continua medindo, mas não codifica nem envia (ninguém assistindo).</summary>
    bool Paused { get; set; }

    string Description { get; }

    event Action<AudioPacket>? PacketReady;

    void Start();
}

/// <summary>Opus pelo FFmpeg (libopus). Um quadro entra, um pacote sai, sem fila.</summary>
internal sealed unsafe class OpusEncoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;

    public OpusEncoder(AudioProfile profile)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Profile = profile;
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec is null) throw new InvalidOperationException("Encoder Opus (libopus) não está nesta build do FFmpeg.");
        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->sample_rate = AudioProfile.SampleRate;
        _context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _context->bit_rate = profile.BitrateKbps * 1000L;
        _context->time_base = new AVRational { num = 1, den = AudioProfile.SampleRate };
        ffmpeg.av_channel_layout_default(&_context->ch_layout, AudioProfile.Channels);
        Set("application", profile.OpusApplication);
        Set("frame_duration", profile.FrameMs.ToString());
        Set("vbr", profile.Music ? "constrained" : "off");
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_context, codec, null), "avcodec_open2(libopus)");

        FrameSamples = _context->frame_size > 0 ? _context->frame_size : profile.FrameSamples;
        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = FrameSamples;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->sample_rate = AudioProfile.SampleRate;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, AudioProfile.Channels);
        FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer(audio)");
        _packet = ffmpeg.av_packet_alloc();
    }

    public AudioProfile Profile { get; }
    public int FrameSamples { get; }

    private void Set(string key, string value)
    {
        if (ffmpeg.av_opt_set(_context->priv_data, key, value, 0) < 0) Log.Debug($"libopus não aceitou {key}={value}");
    }

    /// <summary>Codifica exatamente <see cref="FrameSamples"/> amostras estéreo intercaladas.</summary>
    public byte[]? Encode(ReadOnlySpan<float> interleaved)
    {
        if (_context is null) return null;
        ffmpeg.av_frame_make_writable(_frame);
        interleaved[..(FrameSamples * AudioProfile.Channels)].CopyTo(new Span<float>(_frame->data[0], FrameSamples * AudioProfile.Channels));
        _frame->pts = _pts;
        _pts += FrameSamples;
        if (ffmpeg.avcodec_send_frame(_context, _frame) < 0) return null;
        byte[]? result = null;
        while (ffmpeg.avcodec_receive_packet(_context, _packet) >= 0)
        {
            result = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, result, 0, result.Length);
            ffmpeg.av_packet_unref(_packet);
        }
        return result;
    }

    public void Dispose()
    {
        if (_context is not null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
    }
}

/// <summary>
/// Junta amostras convertidas (48 kHz estéreo float) em quadros do Opus. O timestamp de cada quadro vem do relógio
/// de mídia na chegada do buffer, então silêncios (o WASAPI não entrega nada quando nada toca) não atrasam o áudio.
/// </summary>
internal sealed class OpusFramer : IDisposable
{
    private readonly OpusEncoder _encoder;
    private readonly AudioMeter _meter;
    private readonly float[] _frame;
    private int _fill;
    private long _frameStartUs;

    public OpusFramer(AudioProfile profile, AudioMeter meter)
    {
        _encoder = new OpusEncoder(profile);
        _meter = meter;
        _frame = new float[_encoder.FrameSamples * AudioProfile.Channels];
    }

    public bool Paused { get; set; }

    public event Action<AudioPacket>? PacketReady;

    /// <param name="startUs">Instante (relógio de mídia) da primeira amostra do bloco.</param>
    public void Push(ReadOnlySpan<float> interleaved, long startUs)
    {
        _meter.AddInterleaved(interleaved);
        if (Paused)
        {
            _fill = 0;
            return;
        }
        var offset = 0;
        while (offset < interleaved.Length)
        {
            if (_fill == 0) _frameStartUs = startUs + offset / AudioProfile.Channels * 1_000_000L / AudioProfile.SampleRate;
            var take = Math.Min(_frame.Length - _fill, interleaved.Length - offset);
            interleaved.Slice(offset, take).CopyTo(_frame.AsSpan(_fill));
            _fill += take;
            offset += take;
            if (_fill < _frame.Length) break;
            _fill = 0;
            var data = _encoder.Encode(_frame);
            if (data is not null) PacketReady?.Invoke(new AudioPacket(data, _frameStartUs));
        }
    }

    public void Dispose() => _encoder.Dispose();
}

/// <summary>Como separar o som do PC: tirar a voz da rede e os sons do ScreenFlow, e quais apps tirar também.</summary>
public sealed record SystemAudioOptions(bool SeparateApps = true, IReadOnlyList<MutedApp>? MutedApps = null);

/// <summary>
/// Captura o som do PC, converte para 48 kHz estéreo float com o swresample (reamostragem de qualidade e downmix correto
/// de 5.1/7.1, mantendo o canal central) e codifica em Opus. Com <see cref="SystemAudioOptions.SeparateApps"/>, usa o
/// loopback de processo do Windows: sem apps escolhidos, uma captura "tudo menos o ScreenFlow"; com apps escolhidos, a
/// mistura de <see cref="AppAudioMixer"/>. Se isso falhar, cai no loopback normal (todo o som do PC) e avisa.
/// Se o dispositivo padrão de saída mudar (fone Bluetooth, por exemplo), a captura reinicia sozinha no novo.
/// </summary>
public sealed unsafe class SystemAudioCapture : IAudioSource
{
    private readonly object _gate = new();
    private readonly OpusFramer _framer;
    private readonly DeviceWatcher _watcher;
    private SystemAudioOptions _options;
    private IWaveIn? _capture;
    private SwrContext* _resampler;
    private int _sourceRate;
    private int _sourceChannels;
    private int _sourceBits;
    private bool _sourceFloat;
    private float[] _converted = new float[8192];
    private float[] _widened = [];
    private bool _started;
    private bool _disposed;

    public SystemAudioCapture(AudioProfile profile, SystemAudioOptions? options = null)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Profile = profile;
        _options = Clean(options ?? new SystemAudioOptions());
        Meter = new AudioMeter();
        _framer = new OpusFramer(profile, Meter);
        _framer.PacketReady += packet => PacketReady?.Invoke(packet);
        OpenCapture();
        _watcher = new DeviceWatcher(DataFlow.Render, () => Restart("dispositivo de saída padrão mudou"));
    }

    public AudioProfile Profile { get; }
    public AudioMeter Meter { get; }
    public string Description { get; private set; } = "";

    /// <summary>O que está indo: "todo o som do PC", "sem a voz da rede" ou "sem Spotify e a voz da rede".</summary>
    public string Separation { get; private set; } = "";

    /// <summary>O som está separado por app (a voz da rede fica fora). False: vai todo o som do PC.</summary>
    public bool Separated { get; private set; }

    public bool Paused
    {
        get => _framer.Paused;
        set => _framer.Paused = value;
    }

    public event Action<AudioPacket>? PacketReady;

    /// <summary>Aviso para quem transmite (a separação por app não funcionou e foi todo o som do PC, por exemplo).</summary>
    public event Action<string>? Notice;

    /// <summary>O último aviso (o da abertura sai antes de alguém assinar o <see cref="Notice"/>).</summary>
    public string? LastNotice { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            _capture?.StartRecording();
        }
    }

    /// <summary>Troca os apps que ficam fora (vale na hora, durante a transmissão).</summary>
    public void Configure(SystemAudioOptions options)
    {
        options = Clean(options);
        lock (_gate)
        {
            if (options.SeparateApps == _options.SeparateApps && options.MutedApps!.Select(a => a.Exe).SequenceEqual(_options.MutedApps!.Select(a => a.Exe))) return;
            _options = options;
        }
        Restart("apps tirados do som mudaram", delayMs: 0);
    }

    private static SystemAudioOptions Clean(SystemAudioOptions options) => options with { MutedApps = MutedApp.Normalize(options.MutedApps) };

    private void OpenCapture()
    {
        var capture = CreateCapture(out var separation);
        var format = capture.WaveFormat;
        _sourceRate = format.SampleRate;
        _sourceChannels = Math.Max(1, format.Channels);
        _sourceBits = format.BitsPerSample;
        _sourceFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        CreateResampler();
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        _capture = capture;
        Separation = separation;
        Separated = capture is not WasapiLoopbackCapture;
        Description = $"{_sourceRate} Hz, {_sourceChannels} canais, {_sourceBits} bits{(_sourceFloat ? " float" : string.Empty)} → Opus {Profile.BitrateKbps} kbps"
                      + (Profile.Music ? " (música, 20 ms, VBR)" : " (baixa latência, 10 ms)") + $" · {separation}";
        Log.Info($"Áudio do sistema: {Description}");
    }

    /// <summary>Escolhe a captura: mistura sem os apps escolhidos, tudo menos o ScreenFlow ou, se nada disso abrir, tudo.</summary>
    private IWaveIn CreateCapture(out string separation)
    {
        var options = _options;
        if (options.SeparateApps && ProcessLoopbackCapture.Supported)
        {
            if (options.MutedApps is { Count: > 0 } muted)
            {
                try
                {
                    var mixer = AppAudioMixer.Open(muted.Select(a => a.Exe));
                    separation = $"sem {MutedApp.JoinNames([.. muted.Select(a => a.Name), "a voz da rede"])}";
                    return mixer;
                }
                catch (Exception exception)
                {
                    Log.Warn($"Não deu para separar o som por app: {exception.Message}");
                    Warn($"Não deu para tirar o som de {MutedApp.JoinNames(muted.Select(a => a.Name).ToList())} da transmissão ({exception.Message}).");
                }
            }
            try
            {
                var capture = ProcessLoopbackCapture.ExcludingProcess(Environment.ProcessId);
                separation = "sem a voz da rede";
                return capture;
            }
            catch (Exception exception)
            {
                Log.Warn($"Não deu para tirar os sons do ScreenFlow do som do PC: {exception.Message}");
                Warn($"O som do PC vai completo, com a voz da rede junto: o Windows não deixou separar ({exception.Message}).");
            }
        }
        separation = "todo o som do PC";
        return new WasapiLoopbackCapture();
    }

    private void Warn(string message)
    {
        if (message == LastNotice) return;
        LastNotice = message;
        Notice?.Invoke(message);
    }

    private void CreateResampler()
    {
        FreeResampler();
        // 24 bits não existe no swresample: vira float antes (ver Widen).
        var inputFormat = _sourceFloat && _sourceBits == 32 ? AVSampleFormat.AV_SAMPLE_FMT_FLT
            : _sourceBits switch
            {
                16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
                32 => AVSampleFormat.AV_SAMPLE_FMT_S32,
                _ => AVSampleFormat.AV_SAMPLE_FMT_FLT,
            };
        AVChannelLayout input, output;
        ffmpeg.av_channel_layout_default(&input, _sourceChannels);
        ffmpeg.av_channel_layout_default(&output, AudioProfile.Channels);
        SwrContext* context = null;
        FFmpegLoader.Check(ffmpeg.swr_alloc_set_opts2(&context, &output, AVSampleFormat.AV_SAMPLE_FMT_FLT, AudioProfile.SampleRate,
            &input, inputFormat, _sourceRate, 0, null), "swr_alloc_set_opts2");
        var result = ffmpeg.swr_init(context);
        ffmpeg.av_channel_layout_uninit(&input);
        ffmpeg.av_channel_layout_uninit(&output);
        if (result < 0)
        {
            ffmpeg.swr_free(&context);
            FFmpegLoader.Check(result, "swr_init");
        }
        _resampler = context;
    }

    private void FreeResampler()
    {
        if (_resampler is null) return;
        var context = _resampler;
        ffmpeg.swr_free(&context);
        _resampler = null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_gate)
        {
            if (_disposed || _resampler is null || !ReferenceEquals(sender, _capture)) return;
            var now = MediaClock.NowUs;
            var bytesPerFrame = _sourceBits / 8 * _sourceChannels;
            var frames = e.BytesRecorded / bytesPerFrame;
            if (frames <= 0) return;
            var startUs = now - frames * 1_000_000L / _sourceRate;

            var maxOut = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(_resampler, _sourceRate) + frames, AudioProfile.SampleRate, _sourceRate, AVRounding.AV_ROUND_UP);
            if (_converted.Length < maxOut * AudioProfile.Channels) _converted = new float[maxOut * AudioProfile.Channels * 2];

            int produced;
            fixed (byte* raw = e.Buffer)
            fixed (float* destination = _converted)
            {
                var input = raw;
                if (_sourceBits == 24) input = Widen(raw, frames);
                var output = (byte*)destination;
                produced = ffmpeg.swr_convert(_resampler, &output, maxOut, &input, frames);
            }
            if (produced > 0) _framer.Push(_converted.AsSpan(0, produced * AudioProfile.Channels), startUs);
        }
    }

    /// <summary>PCM de 24 bits empacotado → float (o swresample não lê 24 bits).</summary>
    private byte* Widen(byte* raw, int frames)
    {
        var count = frames * _sourceChannels;
        if (_widened.Length < count) _widened = GC.AllocateUninitializedArray<float>(count * 2, pinned: true);
        for (var i = 0; i < count; i++)
        {
            var p = raw + i * 3;
            _widened[i] = ((p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8) / 8388608f;
        }
        return (byte*)Unsafe.AsPointer(ref _widened[0]);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _disposed || !ReferenceEquals(sender, _capture)) return;
        Log.Warn($"Captura de áudio parou: {e.Exception.Message}");
        Restart("a captura parou");
    }

    /// <summary>
    /// Fecha a captura atual e abre outra. A antiga é liberada fora da trava: a thread dela pode estar esperando a trava
    /// dentro do OnData, e liberar segurando a trava travaria as duas.
    /// </summary>
    private void Restart(string reason, int delayMs = 400)
    {
        _ = Task.Run(() =>
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            IWaveIn? old;
            lock (_gate)
            {
                if (_disposed) return;
                Log.Info($"Reiniciando a captura de áudio ({reason}).");
                old = DetachCapture();
            }
            Release(old);
            lock (_gate)
            {
                if (_disposed || _capture is not null) return;
                try
                {
                    OpenCapture();
                    if (_started) _capture?.StartRecording();
                }
                catch (Exception exception) { Log.Warn($"Não deu para reabrir a captura de áudio: {exception.Message}"); }
            }
        });
    }

    private IWaveIn? DetachCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return null;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        return capture;
    }

    private static void Release(IWaveIn? capture)
    {
        if (capture is null) return;
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        IWaveIn? capture;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            capture = DetachCapture();
        }
        Release(capture);
        lock (_gate)
        {
            FreeResampler();
            _framer.Dispose();
        }
    }
}

/// <summary>Tom de teste (acorde que muda a cada meio segundo). Usado no autoteste e nas capturas de tela.</summary>
public sealed class ToneAudioSource : IAudioSource
{
    private static readonly double[][] Chords = [[261.63, 329.63, 392.00], [220.00, 261.63, 329.63], [174.61, 220.00, 261.63], [196.00, 246.94, 293.66]];
    private readonly OpusFramer _framer;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public ToneAudioSource(AudioProfile profile)
    {
        Profile = profile;
        Meter = new AudioMeter();
        _framer = new OpusFramer(profile, Meter);
        _framer.PacketReady += packet => PacketReady?.Invoke(packet);
    }

    public AudioProfile Profile { get; }
    public AudioMeter Meter { get; }
    public string Description => $"tom de teste → Opus {Profile.BitrateKbps} kbps";
    public long PacketsProduced { get; private set; }

    public bool Paused
    {
        get => _framer.Paused;
        set => _framer.Paused = value;
    }

    public event Action<AudioPacket>? PacketReady;

    public void Start()
    {
        _framer.PacketReady += _ => PacketsProduced++;
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenFlow tom de teste" };
        _thread.Start();
    }

    private void Run()
    {
        const int blockMs = 10;
        var samples = AudioProfile.SampleRate * blockMs / 1000;
        var block = new float[samples * AudioProfile.Channels];
        long position = 0;
        var clock = Stopwatch.StartNew();
        var blocks = 0L;
        while (!_stop.IsCancellationRequested)
        {
            var chord = Chords[position / (AudioProfile.SampleRate / 2) % Chords.Length];
            for (var i = 0; i < samples; i++)
            {
                var t = (position + i) / (double)AudioProfile.SampleRate;
                var value = 0.0;
                foreach (var hz in chord) value += Math.Sin(2 * Math.PI * hz * t);
                var sample = (float)(value * 0.22);
                block[i * 2] = sample;
                block[i * 2 + 1] = sample;
            }
            _framer.Push(block, MediaClock.NowUs - blockMs * 1000);
            position += samples;
            blocks++;
            var wait = blocks * blockMs - clock.ElapsedMilliseconds;
            if (wait > 0) _stop.Token.WaitHandle.WaitOne((int)wait);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(1000);
        _framer.Dispose();
    }
}

/// <summary>Avisa quando o dispositivo padrão de áudio muda (para reabrir captura ou reprodução).</summary>
[ComVisible(true)]
internal sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly DataFlow _flow;
    private readonly Action _changed;
    private readonly MMDeviceEnumerator? _enumerator;
    private string? _lastDefault;

    public DeviceWatcher(DataFlow flow, Action changed)
    {
        _flow = flow;
        _changed = changed;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception exception)
        {
            Log.Debug($"Sem aviso de troca de dispositivo de áudio: {exception.Message}");
            _enumerator = null;
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != _flow || role != Role.Multimedia || defaultDeviceId == _lastDefault) return;
        _lastDefault = defaultDeviceId;
        _changed();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_enumerator is null) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }
}

/// <summary>
/// Buffer de reprodução com alvo de latência: espera juntar o alvo antes de tocar, volta a juntar depois de um
/// buraco (em vez de estalar) e pula para o alvo quando a fila passa do máximo (atraso acumulado).
/// </summary>
internal sealed class JitterBuffer : ISampleProvider
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _target;
    private readonly int _max;
    private int _read;
    private int _count;
    private bool _playing;

    public JitterBuffer(int targetMs, int maxMs)
    {
        _target = Floats(targetMs);
        _max = Floats(maxMs);
        _ring = new float[_max + Floats(200)];
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioProfile.SampleRate, AudioProfile.Channels);
    public float Volume { get; set; } = 1f;
    public long Underruns { get; private set; }
    public long Skips { get; private set; }

    public TimeSpan Buffered
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_count / (double)(AudioProfile.SampleRate * AudioProfile.Channels)); }
    }

    private static int Floats(int ms) => AudioProfile.SampleRate * ms / 1000 * AudioProfile.Channels;

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length > _ring.Length) samples = samples[^_ring.Length..];
            var overflow = _count + samples.Length - _ring.Length;
            if (overflow > 0) Drop(overflow);
            var write = (_read + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - write);
            samples[..first].CopyTo(_ring.AsSpan(write));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
            if (_count > _max)
            {
                Drop(_count - _target);
                Skips++;
            }
        }
    }

    private void Drop(int floats)
    {
        floats = Math.Min(_count, floats + (floats & 1)); // mantém L/R alinhados
        _read = (_read + floats) % _ring.Length;
        _count -= floats;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = _count = 0;
            _playing = false;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (!_playing)
            {
                if (_count < _target)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }
                _playing = true;
            }
            var n = Math.Min(count, _count);
            var first = Math.Min(n, _ring.Length - _read);
            var volume = Volume;
            for (var i = 0; i < first; i++) buffer[offset + i] = _ring[_read + i] * volume;
            for (var i = first; i < n; i++) buffer[offset + i] = _ring[i - first] * volume;
            _read = (_read + n) % _ring.Length;
            _count -= n;
            if (n < count)
            {
                Array.Clear(buffer, offset + n, count - n);
                _playing = false;
                Underruns++;
            }
            return count;
        }
    }
}

/// <summary>
/// Decodifica Opus e toca pelo WASAPI. Na tela e na câmera o buffer é curto e o áudio atrasado em relação ao vídeo
/// é descartado (sincronia); na festa de música o buffer é maior e a redundância cobre perdas isoladas.
/// </summary>
public sealed unsafe class AudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly JitterBuffer _buffer;
    private readonly bool _outputEnabled;
    private readonly DeviceWatcher? _watcher;
    private WasapiOut? _output;
    private AVCodecContext* _decoder;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private float[] _interleaved = new float[4096];
    private uint? _lastSequence;
    private bool _disposed;

    public AudioPlayer(float volume, AudioProfile profile, bool outputEnabled = true)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Profile = profile;
        _outputEnabled = outputEnabled;
        _buffer = new JitterBuffer(profile.PlaybackTargetMs, profile.PlaybackMaxMs) { Volume = Math.Clamp(volume, 0f, 1f) };

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
        if (codec is null) throw new InvalidOperationException("Decoder Opus não está nesta build do FFmpeg.");
        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        _decoder->sample_rate = AudioProfile.SampleRate;
        ffmpeg.av_channel_layout_default(&_decoder->ch_layout, AudioProfile.Channels);
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_decoder, codec, null), "avcodec_open2(opus)");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();

        if (outputEnabled)
        {
            OpenOutput();
            _watcher = new DeviceWatcher(DataFlow.Render, () => Task.Run(() =>
            {
                Thread.Sleep(400);
                lock (_gate)
                {
                    if (_disposed) return;
                    Log.Info("Saída de áudio padrão mudou: tocando no novo dispositivo.");
                    CloseOutput();
                    try { OpenOutput(); } catch (Exception exception) { Log.Warn($"Não deu para abrir a nova saída de áudio: {exception.Message}"); }
                }
            }));
        }
    }

    public AudioProfile Profile { get; }
    public AudioMeter Meter { get; } = new();
    public long PacketsDecoded { get; private set; }
    public long PacketsRecovered { get; private set; }
    public long Underruns => _buffer.Underruns;

    public float Volume
    {
        get => _buffer.Volume;
        set => _buffer.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Latência atual do buffer de áudio.</summary>
    public TimeSpan Buffered => _buffer.Buffered;

    private void OpenOutput()
    {
        _output = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        _output.Init(_buffer);
        _output.Play();
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }

    /// <param name="lagMs">Quanto este pacote está atrasado em relação ao último frame de vídeo mostrado.</param>
    public void Play(ReadOnlySpan<byte> data, double lagMs)
    {
        // Mais de 80 ms atrás do vídeo: descarta para alcançar (o vídeo é mostrado assim que chega).
        if (lagMs > 80) return;
        lock (_gate) Decode(data);
    }

    /// <summary>Pacote da festa de música: quadro atual + anterior. Recupera um quadro perdido quando dá.</summary>
    public void PlayPair(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (!AudioPair.TryUnpack(payload, out var current, out var previous)) return;
        lock (_gate)
        {
            if (_lastSequence is { } last)
            {
                var gap = sequence - last;
                if (gap == 0 || gap > int.MaxValue) return; // repetido ou atrasado
                if (gap == 2 && !previous.IsEmpty)
                {
                    Decode(previous);
                    PacketsRecovered++;
                }
            }
            _lastSequence = sequence;
            Decode(current);
        }
    }

    private void Decode(ReadOnlySpan<byte> data)
    {
        if (_decoder is null || data.IsEmpty) return;
        fixed (byte* pointer = data)
        {
            _packet->data = pointer;
            _packet->size = data.Length;
            var sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0) return;
        }
        while (ffmpeg.avcodec_receive_frame(_decoder, _frame) >= 0)
        {
            var samples = _frame->nb_samples;
            var channels = Math.Max(1, _frame->ch_layout.nb_channels);
            var needed = samples * 2;
            if (_interleaved.Length < needed) _interleaved = new float[needed * 2];
            var planar = _frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            var left = (float*)_frame->data[0];
            var right = planar && channels > 1 ? (float*)_frame->data[1] : left;
            for (var i = 0; i < samples; i++)
            {
                if (planar)
                {
                    _interleaved[i * 2] = left[i];
                    _interleaved[i * 2 + 1] = right[i];
                }
                else
                {
                    _interleaved[i * 2] = left[i * channels];
                    _interleaved[i * 2 + 1] = left[i * channels + (channels > 1 ? 1 : 0)];
                }
            }
            var span = _interleaved.AsSpan(0, needed);
            Meter.AddInterleaved(span);
            if (_outputEnabled) _buffer.Write(span);
            PacketsDecoded++;
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseOutput();
            if (_decoder is not null) { var c = _decoder; ffmpeg.avcodec_free_context(&c); _decoder = null; }
            if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        }
    }
}
