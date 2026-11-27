using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Infrastructure.Media;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>Formato da voz da rede: 48 kHz, mono, quadros de 20 ms.</summary>
public static class VoiceFormat
{
    public const int SampleRate = 48_000;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000;
    public const int BitrateKbps = 32;
}

/// <summary>De onde vem a voz: o microfone ou, no autoteste, um tom.</summary>
public interface IVoiceInput : IDisposable
{
    string Description { get; }

    /// <summary>Um quadro de <see cref="VoiceFormat.FrameSamples"/> amostras mono. O array é reaproveitado.</summary>
    event Action<float[]>? FrameReady;

    void Start();
}

public sealed record MicrophoneInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Uma saída de som (fone, caixa) para a voz da sala.</summary>
public sealed record SpeakerInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class Speakers
{
    /// <summary>Saídas de som ligadas no Windows.</summary>
    public static IReadOnlyList<SpeakerInfo> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new SpeakerInfo(d.ID, d.FriendlyName)).OrderBy(d => d.Name).ToArray();
        }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível listar as saídas de som: {exception.Message}");
            return [];
        }
    }
}

public static class Microphones
{
    /// <summary>Microfones ligados no Windows.</summary>
    public static IReadOnlyList<MicrophoneInfo> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new MicrophoneInfo(d.ID, d.FriendlyName)).OrderBy(d => d.Name).ToArray();
        }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível listar os microfones: {exception.Message}");
            return [];
        }
    }
}

/// <summary>
/// Microfone por WASAPI (modo compartilhado), convertido para 48 kHz mono com o swresample e com um filtro que tira o
/// ronco grave (vento, mesa). Sem um microfone escolhido, segue o padrão do Windows e troca sozinho quando ele muda.
/// </summary>
public sealed unsafe class MicrophoneInput : IVoiceInput
{
    private readonly object _gate = new();
    private readonly string? _deviceId;
    private readonly DeviceWatcher? _watcher;
    private readonly float[] _frame = new float[VoiceFormat.FrameSamples];
    private WasapiCapture? _capture;
    private SwrContext* _resampler;
    private int _sourceRate;
    private int _sourceChannels;
    private int _sourceBits;
    private bool _sourceFloat;
    private float[] _converted = new float[8192];
    private float[] _widened = [];
    private int _fill;
    private float _highPassIn;
    private float _highPassOut;
    private bool _started;
    private bool _disposed;

    public MicrophoneInput(string? deviceId)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        _deviceId = deviceId;
        Open();
        if (deviceId is null) _watcher = new DeviceWatcher(DataFlow.Capture, () => Restart("microfone padrão mudou"));
    }

    public string Description { get; private set; } = "";

    public event Action<float[]>? FrameReady;

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            _capture?.StartRecording();
        }
    }

    private void Open()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        if (_deviceId is not null)
        {
            try { device = enumerator.GetDevice(_deviceId); }
            catch (Exception) { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        }
        else device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

        var capture = new WasapiCapture(device, true, 20);
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
        Description = $"{device.FriendlyName} · {_sourceRate} Hz, {_sourceChannels} canais → Opus {VoiceFormat.BitrateKbps} kbps";
        Log.Info($"Microfone da voz: {Description}");
    }

    private void CreateResampler()
    {
        FreeResampler();
        var inputFormat = _sourceFloat && _sourceBits == 32 ? AVSampleFormat.AV_SAMPLE_FMT_FLT
            : _sourceBits switch
            {
                16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
                32 => AVSampleFormat.AV_SAMPLE_FMT_S32,
                _ => AVSampleFormat.AV_SAMPLE_FMT_FLT,
            };
        AVChannelLayout input, output;
        ffmpeg.av_channel_layout_default(&input, _sourceChannels);
        ffmpeg.av_channel_layout_default(&output, 1);
        SwrContext* context = null;
        FFmpegLoader.Check(ffmpeg.swr_alloc_set_opts2(&context, &output, AVSampleFormat.AV_SAMPLE_FMT_FLT, VoiceFormat.SampleRate,
            &input, inputFormat, _sourceRate, 0, null), "swr_alloc_set_opts2(microfone)");
        var result = ffmpeg.swr_init(context);
        ffmpeg.av_channel_layout_uninit(&input);
        ffmpeg.av_channel_layout_uninit(&output);
        if (result < 0)
        {
            ffmpeg.swr_free(&context);
            FFmpegLoader.Check(result, "swr_init(microfone)");
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
            var bytesPerFrame = _sourceBits / 8 * _sourceChannels;
            var frames = e.BytesRecorded / bytesPerFrame;
            if (frames <= 0) return;
            var maxOut = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(_resampler, _sourceRate) + frames, VoiceFormat.SampleRate, _sourceRate, AVRounding.AV_ROUND_UP);
            if (_converted.Length < maxOut) _converted = new float[maxOut * 2];
            int produced;
            fixed (byte* raw = e.Buffer)
            fixed (float* destination = _converted)
            {
                var input = raw;
                if (_sourceBits == 24) input = Widen(raw, frames);
                var output = (byte*)destination;
                produced = ffmpeg.swr_convert(_resampler, &output, maxOut, &input, frames);
            }
            if (produced > 0) Push(_converted.AsSpan(0, produced));
        }
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            // Passa-altas de 1ª ordem (~90 Hz): tira ronco e "pop" de ar sem mexer na voz.
            var filtered = 0.988f * (_highPassOut + sample - _highPassIn);
            _highPassIn = sample;
            _highPassOut = filtered;
            _frame[_fill++] = filtered;
            if (_fill < _frame.Length) continue;
            _fill = 0;
            FrameReady?.Invoke(_frame);
        }
    }

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
        if (e.Exception is null || _disposed) return;
        Log.Warn($"O microfone parou: {e.Exception.Message}");
        Restart("o microfone parou");
    }

    private void Restart(string reason)
    {
        _ = Task.Run(() =>
        {
            Thread.Sleep(400);
            lock (_gate)
            {
                if (_disposed) return;
                Log.Info($"Reabrindo o microfone ({reason}).");
                Close();
                try
                {
                    Open();
                    if (_started) _capture?.StartRecording();
                }
                catch (Exception exception) { Log.Warn($"Não deu para reabrir o microfone: {exception.Message}"); }
            }
        });
    }

    private void Close()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            FreeResampler();
        }
    }
}

/// <summary>
/// "Ouvir meu microfone": toca o próprio microfone na saída padrão do Windows (o fone), com o menor atraso possível:
/// buffer adaptativo a partir de 20 ms e WASAPI de 20 ms. Recebe os mesmos quadros que vão para a voz (48 kHz mono, já
/// sem o ronco grave), então a pessoa ouve o microfone do jeito que ele chega no ScreenFlow. Se a saída padrão mudar
/// (fone Bluetooth), passa a tocar nela.
/// </summary>
public sealed class MicrophoneMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly JitterBuffer _buffer = new(20, 100);
    private readonly VolumeSampleProvider _volume;
    private readonly float[] _stereo = new float[VoiceFormat.FrameSamples * AudioProfile.Channels];
    private readonly DeviceWatcher _watcher;
    private WasapiOut? _output;
    private bool _disposed;

    public MicrophoneMonitor(float volume = 1f)
    {
        _volume = new VolumeSampleProvider(_buffer) { Volume = Math.Clamp(volume, 0f, 1.5f) };
        Open();
        _watcher = new DeviceWatcher(DataFlow.Render, () => Task.Run(() =>
        {
            Thread.Sleep(400);
            lock (_gate)
            {
                if (_disposed) return;
                Close();
                try { Open(); } catch (Exception exception) { Log.Warn($"Não deu para ouvir o microfone na nova saída: {exception.Message}"); }
            }
        }));
    }

    /// <summary>Quanto o retorno está atrasado agora (buffer), para o teste.</summary>
    public int TargetMs => _buffer.TargetMs;

    /// <summary>Um quadro do microfone (mono). Chamado pela thread da captura.</summary>
    public void Write(float[] mono)
    {
        var count = Math.Min(mono.Length, VoiceFormat.FrameSamples);
        for (var i = 0; i < count; i++) _stereo[i * 2] = _stereo[i * 2 + 1] = mono[i];
        _buffer.Write(_stereo.AsSpan(0, count * 2));
    }

    private void Open()
    {
        var output = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        output.Init(_volume);
        output.Play();
        _output = output;
    }

    private void Close()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}

/// <summary>Voz de teste: "fala" (um tom com vibrato) por um tempo e fica em silêncio, em ciclo. Usada no autoteste.</summary>
public sealed class ToneVoiceInput : IVoiceInput
{
    private readonly CancellationTokenSource _stop = new();
    private readonly float[] _frame = new float[VoiceFormat.FrameSamples];
    private Thread? _thread;

    public string Description => "voz de teste (tom)";
    public long FramesProduced { get; private set; }

    public event Action<float[]>? FrameReady;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenFlow voz de teste" };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long position = 0;
        var frames = 0L;
        while (!_stop.IsCancellationRequested)
        {
            var talking = position / VoiceFormat.SampleRate % 3 != 2; // 2 s falando, 1 s quieto
            for (var i = 0; i < _frame.Length; i++)
            {
                var t = (position + i) / (double)VoiceFormat.SampleRate;
                _frame[i] = talking ? (float)(0.25 * Math.Sin(2 * Math.PI * (220 + 30 * Math.Sin(2 * Math.PI * 5 * t)) * t)) : 0f;
            }
            position += _frame.Length;
            FrameReady?.Invoke(_frame);
            FramesProduced++;
            frames++;
            var wait = frames * VoiceFormat.FrameMs - clock.ElapsedMilliseconds;
            if (wait > 0) _stop.Token.WaitHandle.WaitOne((int)wait);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(1000);
    }
}

/// <summary>Opus para voz (libopus, modo "voip", mono, 20 ms, VBR).</summary>
internal sealed unsafe class VoiceEncoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;

    public VoiceEncoder(int bitrateKbps = VoiceFormat.BitrateKbps)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec is null) throw new InvalidOperationException("Encoder Opus (libopus) não está nesta build do FFmpeg.");
        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->sample_rate = VoiceFormat.SampleRate;
        _context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _context->bit_rate = bitrateKbps * 1000L;
        _context->time_base = new AVRational { num = 1, den = VoiceFormat.SampleRate };
        ffmpeg.av_channel_layout_default(&_context->ch_layout, 1);
        Set("application", "voip");
        Set("frame_duration", VoiceFormat.FrameMs.ToString());
        Set("vbr", "on");
        Set("packet_loss", "8");
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_context, codec, null), "avcodec_open2(libopus voz)");
        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = VoiceFormat.FrameSamples;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->sample_rate = VoiceFormat.SampleRate;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, 1);
        FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer(voz)");
        _packet = ffmpeg.av_packet_alloc();
    }

    private void Set(string key, string value)
    {
        if (ffmpeg.av_opt_set(_context->priv_data, key, value, 0) < 0) Log.Debug($"libopus (voz) não aceitou {key}={value}");
    }

    public byte[]? Encode(ReadOnlySpan<float> mono)
    {
        if (_context is null || mono.Length < VoiceFormat.FrameSamples) return null;
        ffmpeg.av_frame_make_writable(_frame);
        mono[..VoiceFormat.FrameSamples].CopyTo(new Span<float>(_frame->data[0], VoiceFormat.FrameSamples));
        _frame->pts = _pts;
        _pts += VoiceFormat.FrameSamples;
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

/// <summary>Decoder Opus de uma pessoa. Entrega estéreo intercalado (a voz mono vai igual nos dois lados).</summary>
internal sealed unsafe class VoiceDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private float[] _output = new float[VoiceFormat.FrameSamples * 4];

    public VoiceDecoder()
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
        if (codec is null) throw new InvalidOperationException("Decoder Opus não está nesta build do FFmpeg.");
        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->sample_rate = VoiceFormat.SampleRate;
        ffmpeg.av_channel_layout_default(&_context->ch_layout, 1);
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_context, codec, null), "avcodec_open2(opus voz)");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Decodifica um quadro e devolve as amostras estéreo (válidas até a próxima chamada).</summary>
    public ReadOnlySpan<float> Decode(ReadOnlySpan<byte> data)
    {
        if (_context is null || data.IsEmpty) return default;
        fixed (byte* pointer = data)
        {
            _packet->data = pointer;
            _packet->size = data.Length;
            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0) return default;
        }
        var written = 0;
        while (ffmpeg.avcodec_receive_frame(_context, _frame) >= 0)
        {
            var samples = _frame->nb_samples;
            var channels = Math.Max(1, _frame->ch_layout.nb_channels);
            if (_output.Length < written + samples * 2) Array.Resize(ref _output, (written + samples * 2) * 2);
            var planar = _frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            var data0 = (float*)_frame->data[0];
            for (var i = 0; i < samples; i++)
            {
                var value = planar ? data0[i] : data0[i * channels];
                _output[written + i * 2] = value;
                _output[written + i * 2 + 1] = value;
            }
            written += samples * 2;
            ffmpeg.av_frame_unref(_frame);
        }
        return _output.AsSpan(0, written);
    }

    public void Dispose()
    {
        if (_context is not null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
    }
}

/// <summary>Sons curtos da voz (como os do Discord): alguém entrou, saiu, microfone mudo ou ligado.</summary>
public enum VoiceCue { Join, Leave, Mute, Unmute }

/// <summary>
/// Saída da voz: cada pessoa tem um buffer de jitter, todos são mixados e tocam por um WASAPI só. Os sons de aviso entram
/// no mesmo mix. Se a saída padrão do Windows mudar (fone Bluetooth), passa a tocar nela.
/// </summary>
internal sealed class VoiceOutput : IDisposable
{
    private readonly object _gate = new();
    // As vozes passam por um ganho próprio (zerado ao ensurdecer); os avisos entram direto no mix final, então o som de
    // ensurdecer ainda toca.
    private readonly MixingSampleProvider _voices = new(WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, AudioProfile.Channels)) { ReadFully = true };
    private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, AudioProfile.Channels)) { ReadFully = true };
    private readonly VolumeSampleProvider _voiceGain;
    private readonly VolumeSampleProvider _volume;
    private readonly DeviceWatcher _watcher;
    private WasapiOut? _output;
    private float _level = 1f;
    private bool _deafened;
    private bool _disposed;

    private readonly string? _speakerId;

    /// <summary>Saída da voz: a escolhida (<paramref name="speakerId"/>) ou, sem escolha (ou desligada), a padrão do Windows.</summary>
    public VoiceOutput(float volume, string? speakerId = null)
    {
        _speakerId = speakerId;
        _voiceGain = new VolumeSampleProvider(_voices);
        _mixer.AddMixerInput(_voiceGain);
        _volume = new VolumeSampleProvider(_mixer);
        Volume = volume;
        Open();
        _watcher = new DeviceWatcher(DataFlow.Render, () => Task.Run(() =>
        {
            Thread.Sleep(400);
            lock (_gate)
            {
                if (_disposed) return;
                Close();
                try { Open(); } catch (Exception exception) { Log.Warn($"Não deu para abrir a nova saída da voz: {exception.Message}"); }
            }
        }));
    }

    public float Volume
    {
        get => _level;
        set
        {
            _level = Math.Clamp(value, 0f, 1.5f);
            Apply();
        }
    }

    /// <summary>Fones desligados (ensurdecer): as vozes param de tocar; só os avisos continuam.</summary>
    public bool Deafened
    {
        get => _deafened;
        set
        {
            _deafened = value;
            Apply();
        }
    }

    private void Apply()
    {
        _voiceGain.Volume = _deafened ? 0f : 1f;
        _volume.Volume = _level;
    }

    private void Open()
    {
        var output = OpenDevice();
        output.Init(_volume);
        output.Play();
        _output = output;
    }

    private WasapiOut OpenDevice()
    {
        if (_speakerId is { } id)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            }
            catch (Exception exception) { Log.Info($"A saída escolhida para a voz não está disponível ({exception.Message}); usando a padrão."); }
        }
        return new WasapiOut(AudioClientShareMode.Shared, true, 40);
    }

    private void Close()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }

    public JitterBuffer AddStream()
    {
        // Voz em quadros de 20 ms: começa em 40 ms e só cresce se a voz de alguém falhar.
        var buffer = new JitterBuffer(40, 240);
        _voices.AddMixerInput(buffer);
        return buffer;
    }

    public void RemoveStream(JitterBuffer buffer) => _voices.RemoveMixerInput(buffer);

    public void Play(VoiceCue cue) => _mixer.AddMixerInput(new CueSampleProvider(cue));

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
        _voices.RemoveAllMixerInputs();
        _mixer.RemoveAllMixerInputs();
    }
}

/// <summary>Um aviso sonoro de poucos milissegundos (duas notas suaves), tocado uma vez e removido do mix.</summary>
internal sealed class CueSampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public CueSampleProvider(VoiceCue cue)
    {
        (double Hz, int Ms)[] notes = cue switch
        {
            VoiceCue.Join => [(587.33, 85), (880.00, 140)],
            VoiceCue.Leave => [(880.00, 85), (587.33, 140)],
            VoiceCue.Mute => [(523.25, 70), (392.00, 90)],
            _ => [(392.00, 70), (523.25, 90)],
        };
        var amplitude = cue is VoiceCue.Mute or VoiceCue.Unmute ? 0.12 : 0.16;
        var total = notes.Sum(n => n.Ms) * VoiceFormat.SampleRate / 1000;
        _samples = new float[total * 2];
        var offset = 0;
        foreach (var (hz, ms) in notes)
        {
            var count = ms * VoiceFormat.SampleRate / 1000;
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)VoiceFormat.SampleRate;
                var attack = Math.Min(1, i / (VoiceFormat.SampleRate * 0.006));
                var release = Math.Min(1, (count - i) / (VoiceFormat.SampleRate * 0.03));
                var tone = Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t);
                var value = (float)(tone * amplitude * attack * release);
                _samples[(offset + i) * 2] = value;
                _samples[(offset + i) * 2 + 1] = value;
            }
            offset += count;
        }
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, AudioProfile.Channels);

    public int Read(float[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _samples.Length - _position);
        if (n <= 0) return 0;
        Array.Copy(_samples, _position, buffer, offset, n);
        _position += n;
        return n;
    }
}
