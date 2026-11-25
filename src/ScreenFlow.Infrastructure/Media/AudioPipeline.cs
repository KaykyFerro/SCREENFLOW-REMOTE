using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Infrastructure.Media;

public readonly record struct AudioPacket(byte[] Data, long TimestampUs);

/// <summary>
/// Captura o áudio do sistema por WASAPI loopback, converte para 48 kHz estéreo float e codifica em Opus
/// em quadros de 10 ms. O timestamp de cada quadro usa o mesmo relógio da captura de vídeo.
/// </summary>
public sealed unsafe class SystemAudioCapture : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private readonly WasapiLoopbackCapture _capture;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly bool _sourceFloat;
    private readonly int _sourceBits;
    private readonly List<float> _pending = new(4096);
    private readonly object _gate = new();
    private AVCodecContext* _encoder;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _frameSize;
    private double _resamplePosition;
    private float _lastLeft, _lastRight;
    private long _samplesEncoded;
    private long _startUs;

    public SystemAudioCapture(int bitrateKbps)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        _capture = new WasapiLoopbackCapture();
        var format = _capture.WaveFormat;
        _sourceRate = format.SampleRate;
        _sourceChannels = format.Channels;
        _sourceBits = format.BitsPerSample;
        _sourceFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        OpenEncoder(bitrateKbps);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Log.Warn($"Captura de áudio parou: {e.Exception.Message}"); };
        Log.Info($"Áudio do sistema: {_sourceRate} Hz, {_sourceChannels} canais, {_sourceBits} bits{(_sourceFloat ? " float" : string.Empty)} → Opus {bitrateKbps} kbps");
    }

    public event Action<AudioPacket>? PacketReady;

    public void Start()
    {
        _startUs = MediaClock.NowUs;
        _capture.StartRecording();
    }

    private void OpenEncoder(int bitrateKbps)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec is null) throw new InvalidOperationException("Encoder Opus (libopus) não está nesta build do FFmpeg.");
        _encoder = ffmpeg.avcodec_alloc_context3(codec);
        _encoder->sample_rate = SampleRate;
        _encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _encoder->bit_rate = bitrateKbps * 1000L;
        ffmpeg.av_channel_layout_default(&_encoder->ch_layout, Channels);
        ffmpeg.av_opt_set(_encoder->priv_data, "application", "lowdelay", 0);
        ffmpeg.av_opt_set(_encoder->priv_data, "frame_duration", "10", 0);
        ffmpeg.av_opt_set(_encoder->priv_data, "vbr", "off", 0);
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_encoder, codec, null), "avcodec_open2(libopus)");
        _frameSize = _encoder->frame_size > 0 ? _encoder->frame_size : 480;
        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = _frameSize;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, Channels);
        FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer(audio)");
        _packet = ffmpeg.av_packet_alloc();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        lock (_gate)
        {
            if (_encoder is null) return;
            var bytesPerSample = _sourceBits / 8;
            var frames = e.BytesRecorded / (bytesPerSample * _sourceChannels);
            var step = _sourceRate / (double)SampleRate;
            fixed (byte* raw = e.Buffer)
            {
                // Reamostragem linear simples para 48 kHz quando o dispositivo usa outra taxa.
                while (_resamplePosition < frames)
                {
                    var index = (int)_resamplePosition;
                    var frac = (float)(_resamplePosition - index);
                    var (l0, r0) = index == 0 ? (_lastLeft, _lastRight) : Read(raw, index - 1, bytesPerSample);
                    var (l1, r1) = Read(raw, index, bytesPerSample);
                    _pending.Add(l0 + (l1 - l0) * frac);
                    _pending.Add(r0 + (r1 - r0) * frac);
                    _resamplePosition += step;
                }
                _resamplePosition -= frames;
                (_lastLeft, _lastRight) = Read(raw, frames - 1, bytesPerSample);
            }

            var perFrame = _frameSize * Channels;
            while (_pending.Count >= perFrame)
            {
                ffmpeg.av_frame_make_writable(_frame);
                var destination = new Span<float>(_frame->data[0], perFrame);
                for (var i = 0; i < perFrame; i++) destination[i] = _pending[i];
                _pending.RemoveRange(0, perFrame);
                _frame->pts = _samplesEncoded;
                _samplesEncoded += _frameSize;
                Encode();
            }
        }
    }

    private (float L, float R) Read(byte* raw, int frame, int bytesPerSample)
    {
        var offset = frame * bytesPerSample * _sourceChannels;
        float Sample(int channel)
        {
            var p = raw + offset + Math.Min(channel, _sourceChannels - 1) * bytesPerSample;
            if (_sourceFloat && bytesPerSample == 4) return *(float*)p;
            return bytesPerSample switch
            {
                2 => *(short*)p / 32768f,
                3 => ((p[0] << 8 | p[1] << 16 | p[2] << 24) >> 8) / 8388608f,
                4 => *(int*)p / 2147483648f,
                _ => 0,
            };
        }
        return (Sample(0), Sample(1));
    }

    private void Encode()
    {
        if (ffmpeg.avcodec_send_frame(_encoder, _frame) < 0) return;
        while (ffmpeg.avcodec_receive_packet(_encoder, _packet) >= 0)
        {
            var data = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, data, 0, data.Length);
            // pts em amostras desde o início → timestamp no relógio de mídia
            var timestamp = _startUs + _packet->pts * 1_000_000 / SampleRate;
            ffmpeg.av_packet_unref(_packet);
            PacketReady?.Invoke(new AudioPacket(data, timestamp));
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        lock (_gate)
        {
            if (_encoder is not null) { var c = _encoder; ffmpeg.avcodec_free_context(&c); _encoder = null; }
            if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        }
    }
}

/// <summary>
/// Decodifica Opus e toca pelo WASAPI com buffer curto. Descarta áudio atrasado em relação ao vídeo
/// para manter a sincronia (o vídeo é apresentado assim que chega).
/// </summary>
public sealed unsafe class AudioPlayer : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private readonly WasapiOut _output;
    private AVCodecContext* _decoder;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private float[] _interleaved = new float[4096];

    public AudioPlayer(float volume)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SystemAudioCapture.SampleRate, SystemAudioCapture.Channels);
        _buffer = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromMilliseconds(300), DiscardOnBufferOverflow = true, ReadFully = true };
        _volume = new VolumeSampleProvider(_buffer.ToSampleProvider()) { Volume = volume };
        _output = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        _output.Init(_volume);

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        _decoder->sample_rate = SystemAudioCapture.SampleRate;
        ffmpeg.av_channel_layout_default(&_decoder->ch_layout, SystemAudioCapture.Channels);
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_decoder, codec, null), "avcodec_open2(opus)");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _output.Play();
    }

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Latência atual do buffer de áudio.</summary>
    public TimeSpan Buffered => _buffer.BufferedDuration;

    /// <param name="lagMs">Quanto este pacote está atrasado em relação ao último frame de vídeo mostrado.</param>
    public void Play(byte[] data, double lagMs)
    {
        // Áudio mais de 80 ms atrás do vídeo: descarta para alcançar. Buffer acima de 100 ms: limpa.
        if (lagMs > 80) return;
        if (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(100)) _buffer.ClearBuffer();

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
            var channels = _frame->ch_layout.nb_channels;
            var needed = samples * 2;
            if (_interleaved.Length < needed) _interleaved = new float[needed];
            var planar = _frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            for (var i = 0; i < samples; i++)
            {
                if (planar)
                {
                    _interleaved[i * 2] = ((float*)_frame->data[0])[i];
                    _interleaved[i * 2 + 1] = ((float*)_frame->data[channels > 1 ? 1u : 0u])[i];
                }
                else
                {
                    _interleaved[i * 2] = ((float*)_frame->data[0])[i * channels];
                    _interleaved[i * 2 + 1] = ((float*)_frame->data[0])[i * channels + (channels > 1 ? 1 : 0)];
                }
            }
            var bytes = MemoryMarshal.AsBytes(_interleaved.AsSpan(0, needed)).ToArray();
            _buffer.AddSamples(bytes, 0, bytes.Length);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        try { _output.Stop(); } catch { }
        _output.Dispose();
        if (_decoder is not null) { var c = _decoder; ffmpeg.avcodec_free_context(&c); _decoder = null; }
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
    }
}
