using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Infrastructure.Capture;
using D3D = Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenFlow.Infrastructure.Media;

public sealed record EncoderSettings(string EncoderName, VideoCodec Codec, int Width, int Height, int Fps, int BitrateKbps);

public readonly record struct EncodedPacket(byte[] Data, bool Keyframe, long TimestampUs);

/// <summary>
/// Encoder de vídeo por hardware via FFmpeg (NVENC, AMF, QuickSync) com preset de baixa latência, sem B-frames
/// e intra-refresh. NVENC e AMF recebem a textura NV12 direto da GPU (zero-copy); QuickSync e x264 recebem
/// o frame por uma cópia de staging.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    private readonly GpuDevice _gpu;
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVPacket* _packet;
    private AVFrame* _cpuFrame;
    private D3D.ID3D11Texture2D? _staging;
    private bool _forceKeyframe = true;

    public VideoEncoder(GpuDevice gpu, EncoderSettings settings)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        _gpu = gpu;
        Settings = settings;
        var codec = ffmpeg.avcodec_find_encoder_by_name(settings.EncoderName);
        if (codec is null) throw new InvalidOperationException($"Encoder {settings.EncoderName} não existe nesta build do FFmpeg.");

        ZeroCopy = settings.EncoderName.EndsWith("_nvenc") || settings.EncoderName.EndsWith("_amf");
        try
        {
            Open(codec, ZeroCopy);
        }
        catch (Exception exception) when (ZeroCopy)
        {
            Log.Warn($"Entrada D3D11 direta falhou em {settings.EncoderName} ({exception.Message}). Usando cópia de staging.");
            Release();
            ZeroCopy = false;
            Open(codec, false);
        }
        _packet = ffmpeg.av_packet_alloc();
        Log.Info($"Encoder {settings.EncoderName} aberto: {settings.Width}x{settings.Height} {settings.Fps} FPS {settings.BitrateKbps} kbps, zero-copy={ZeroCopy}");
    }

    /// <summary>Formato de entrada pela CPU: NV12 para quase todos; I420 para os que não aceitam NV12.</summary>
    public static AVPixelFormat CpuPixelFormat(string encoderName)
        => encoderName is "libopenh264" or "libsvtav1" ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;

    public EncoderSettings Settings { get; private set; }
    public bool ZeroCopy { get; private set; }
    public bool IsHardware => !Settings.EncoderName.StartsWith("lib", StringComparison.Ordinal);

    private void Open(AVCodec* codec, bool gpuInput)
    {
        var s = Settings;
        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = s.Width;
        _context->height = s.Height;
        _context->time_base = new AVRational { num = 1, den = 1_000_000 };
        _context->framerate = new AVRational { num = s.Fps, den = 1 };
        _context->bit_rate = s.BitrateKbps * 1000L;
        _context->rc_max_rate = _context->bit_rate;
        _context->rc_buffer_size = (int)Math.Min(int.MaxValue, _context->bit_rate / s.Fps * 2); // VBV de ~2 frames
        _context->max_b_frames = 0;
        _context->gop_size = s.EncoderName.EndsWith("_nvenc") ? s.Fps : s.Fps * 4;           // nvenc: período do intra-refresh
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;

        if (gpuInput)
        {
            _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            var deviceContext = (AVHWDeviceContext*)_hwDevice->data;
            var d3d = (AVD3D11VADeviceContext*)deviceContext->hwctx;
            Marshal.AddRef(_gpu.Device.NativePointer); // o FFmpeg libera essa referência ao fechar
            d3d->device = (FFmpeg.AutoGen.ID3D11Device*)_gpu.Device.NativePointer;
            FFmpegLoader.Check(ffmpeg.av_hwdevice_ctx_init(_hwDevice), "av_hwdevice_ctx_init");

            _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
            var frames = (AVHWFramesContext*)_hwFrames->data;
            frames->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            frames->width = s.Width;
            frames->height = s.Height;
            frames->initial_pool_size = 4;
            FFmpegLoader.Check(ffmpeg.av_hwframe_ctx_init(_hwFrames), "av_hwframe_ctx_init");
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
        }
        else
        {
            _context->thread_count = s.EncoderName.StartsWith("lib") ? Math.Min(8, Environment.ProcessorCount) : 1;
            _context->pix_fmt = CpuPixelFormat(s.EncoderName);
            _cpuFrame = ffmpeg.av_frame_alloc();
            _cpuFrame->format = (int)_context->pix_fmt;
            _cpuFrame->width = s.Width;
            _cpuFrame->height = s.Height;
            FFmpegLoader.Check(ffmpeg.av_frame_get_buffer(_cpuFrame, 32), "av_frame_get_buffer");
            _staging = _gpu.CreateTexture(s.Width, s.Height, Format.NV12, D3D.BindFlags.None, D3D.ResourceUsage.Staging, D3D.CpuAccessFlags.Read);
        }

        ApplyLowLatencyOptions(s);
        FFmpegLoader.Check(ffmpeg.avcodec_open2(_context, codec, null), $"avcodec_open2({s.EncoderName})");
    }

    private void ApplyLowLatencyOptions(EncoderSettings s)
    {
        var name = s.EncoderName;
        if (name.EndsWith("_nvenc"))
        {
            Set("preset", s.Width * s.Height * (long)s.Fps > 2560L * 1440 * 144 ? "p1" : "p3");
            Set("tune", "ull");
            Set("rc", "cbr");
            Set("zerolatency", "1");
            Set("delay", "0");
            Set("intra-refresh", "1");
            Set("forced-idr", "1");
            Set("multipass", "disabled");
            Set("b_ref_mode", "disabled");
        }
        else if (name.EndsWith("_amf"))
        {
            Set("usage", "ultralowlatency");
            Set("quality", "speed");
            Set("rc", "cbr");
            Set("enforce_hrd", "1");
            Set("header_insertion_mode", "idr");
            Set("latency", "1");
        }
        else if (name.EndsWith("_qsv"))
        {
            Set("preset", "veryfast");
            Set("low_power", "1");
            Set("async_depth", "1");
            Set("look_ahead", "0");
            Set("int_ref_type", "vertical");
            Set("int_ref_cycle_size", Math.Max(2, s.Fps / 4).ToString());
            Set("forced_idr", "1");
        }
        else if (name == "libopenh264")
        {
            Set("allow_skip_frames", "0");
            Set("rc_mode", "bitrate");
        }
        else if (name == "libx264")
        {
            Set("preset", "ultrafast");
            Set("tune", "zerolatency");
            Set("intra-refresh", "1");
        }
    }

    private void Set(string key, string value)
    {
        // Opções que a versão/driver não conhece são ignoradas (retorno negativo).
        if (ffmpeg.av_opt_set(_context->priv_data, key, value, 0) < 0)
            Log.Debug($"Opção {key}={value} não suportada por {Settings.EncoderName}");
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    /// <summary>Ajusta o bitrate sem reabrir o encoder (NVENC reconfigura em tempo real).</summary>
    public bool TryUpdateBitrate(int kbps)
    {
        if (!Settings.EncoderName.EndsWith("_nvenc")) return false;
        _context->bit_rate = kbps * 1000L;
        _context->rc_max_rate = _context->bit_rate;
        _context->rc_buffer_size = (int)Math.Min(int.MaxValue, _context->bit_rate / Settings.Fps * 2);
        Settings = Settings with { BitrateKbps = kbps };
        return true;
    }

    /// <summary>Codifica a textura NV12 (já convertida e escalada na GPU).</summary>
    public void Encode(D3D.ID3D11Texture2D nv12, long timestampUs, List<EncodedPacket> output)
    {
        AVFrame* frame;
        if (ZeroCopy)
        {
            frame = ffmpeg.av_frame_alloc();
            FFmpegLoader.Check(ffmpeg.av_hwframe_get_buffer(_hwFrames, frame, 0), "av_hwframe_get_buffer");
            var target = (nint)frame->data[0];
            var index = (uint)(nint)frame->data[1];
            Marshal.AddRef(target);
            using var texture = new D3D.ID3D11Texture2D(target);
            _gpu.Context.CopySubresourceRegion(texture, index, 0, 0, 0, nv12, 0);
        }
        else
        {
            frame = _cpuFrame;
            ffmpeg.av_frame_make_writable(frame);
            _gpu.Context.CopyResource(_staging!, nv12);
            var mapped = _gpu.Context.Map(_staging!, 0, D3D.MapMode.Read, D3D.MapFlags.None);
            try
            {
                var src = (byte*)mapped.DataPointer;
                var pitch = (int)mapped.RowPitch;
                for (var y = 0; y < Settings.Height; y++)
                    Buffer.MemoryCopy(src + y * pitch, frame->data[0] + y * frame->linesize[0], frame->linesize[0], Settings.Width);
                var uv = src + pitch * Settings.Height;
                if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12)
                {
                    for (var y = 0; y < Settings.Height / 2; y++)
                        Buffer.MemoryCopy(uv + y * pitch, frame->data[1] + y * frame->linesize[1], frame->linesize[1], Settings.Width);
                }
                else
                {
                    // NV12 → I420: separa U e V intercalados.
                    for (var y = 0; y < Settings.Height / 2; y++)
                    {
                        var row = uv + y * pitch;
                        var u = frame->data[1] + y * frame->linesize[1];
                        var v = frame->data[2] + y * frame->linesize[2];
                        for (var x = 0; x < Settings.Width / 2; x++)
                        {
                            u[x] = row[x * 2];
                            v[x] = row[x * 2 + 1];
                        }
                    }
                }
            }
            finally { _gpu.Context.Unmap(_staging!, 0); }
        }

        frame->pts = timestampUs;
        if (_forceKeyframe)
        {
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _forceKeyframe = false;
        }
        else
        {
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        try
        {
            FFmpegLoader.Check(ffmpeg.avcodec_send_frame(_context, frame), "avcodec_send_frame");
        }
        finally
        {
            if (ZeroCopy) ffmpeg.av_frame_free(&frame);
        }
        Drain(output);
    }

    private void Drain(List<EncodedPacket> output)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) break;
            FFmpegLoader.Check(result, "avcodec_receive_packet");
            var data = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, data, 0, data.Length);
            output.Add(new EncodedPacket(data, (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, _packet->pts));
            ffmpeg.av_packet_unref(_packet);
        }
    }

    private void Release()
    {
        if (_context is not null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_hwFrames is not null) { var f = _hwFrames; ffmpeg.av_buffer_unref(&f); _hwFrames = null; }
        if (_hwDevice is not null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
        if (_cpuFrame is not null) { var f = _cpuFrame; ffmpeg.av_frame_free(&f); _cpuFrame = null; }
        _staging?.Dispose();
        _staging = null;
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            ffmpeg.avcodec_send_frame(_context, null); // flush
            var discard = new List<EncodedPacket>();
            try { Drain(discard); } catch { }
        }
        Release();
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
    }
}
