using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>Frame decodificado em BGRA, pronto para a tela. O buffer é reaproveitado entre frames.</summary>
public sealed class DecodedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }
    public long TimestampUs { get; set; }
}

/// <summary>
/// Decoder FFmpeg com aceleração D3D11VA (DXVA) quando disponível, e software como reserva.
/// Configurado para baixa latência: sem fila de reordenação, um frame entra e um frame sai.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly AVCodecContext_get_format _getFormat;
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _transfer;
    private SwsContext* _scaler;
    private int _scalerWidth, _scalerHeight;
    private AVPixelFormat _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private readonly DecodedFrame?[] _buffers = new DecodedFrame?[3];
    private int _bufferIndex;

    public VideoDecoder(VideoCodec codec)
    {
        if (!FFmpegLoader.EnsureLoaded()) throw new InvalidOperationException(FFmpegLoader.Error);
        Codec = codec;
        var id = codec switch
        {
            VideoCodec.Av1 => AVCodecID.AV_CODEC_ID_AV1,
            VideoCodec.Hevc => AVCodecID.AV_CODEC_ID_HEVC,
            _ => AVCodecID.AV_CODEC_ID_H264,
        };
        // O decoder "av1" nativo do FFmpeg só funciona com hwaccel; libdav1d é a reserva por software.
        var decoder = ffmpeg.avcodec_find_decoder(id);
        _getFormat = ChooseFormat;

        if (TryOpen(decoder, hardware: true)) return;
        if (codec == VideoCodec.Av1)
        {
            var dav1d = ffmpeg.avcodec_find_decoder_by_name("libdav1d");
            if (dav1d is not null) decoder = dav1d;
        }
        if (!TryOpen(decoder, hardware: false))
            throw new InvalidOperationException($"Não foi possível abrir o decoder {Quality.CodecLabel(codec)}.");
    }

    public VideoCodec Codec { get; }
    public bool Hardware { get; private set; }
    public string Description => $"{Quality.CodecLabel(Codec)} · " + (Hardware ? "D3D11VA (GPU)" : "software");

    private bool TryOpen(AVCodec* decoder, bool hardware)
    {
        if (decoder is null) return false;
        _context = ffmpeg.avcodec_alloc_context3(decoder);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        if (hardware)
        {
            AVBufferRef* device = null;
            if (ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) < 0)
            {
                Free();
                return false;
            }
            _hwDevice = device;
            _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            _context->get_format = _getFormat;
            _context->thread_count = 1;
        }
        else
        {
            _context->thread_count = Math.Min(8, Environment.ProcessorCount);
            _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        }

        if (ffmpeg.avcodec_open2(_context, decoder, null) < 0)
        {
            Free();
            return false;
        }
        Hardware = hardware;
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _transfer = ffmpeg.av_frame_alloc();
        Log.Info($"Decoder aberto: {Description}");
        return true;
    }

    private AVPixelFormat ChooseFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
        Log.Warn("D3D11VA não oferecido para este stream; decodificando por software.");
        return *formats;
    }

    /// <summary>Decodifica um frame completo. Retorna null quando o decoder ainda não produziu imagem.</summary>
    public DecodedFrame? Decode(byte[] data, long timestampUs)
    {
        DecodedFrame? result = null;
        fixed (byte* pointer = data)
        {
            _packet->data = pointer;
            _packet->size = data.Length;
            _packet->pts = timestampUs;
            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Log.Debug($"send_packet: {FFmpegLoader.ErrorText(sent)}");
                return null;
            }
        }

        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) break;
            if (received < 0)
            {
                Log.Debug($"receive_frame: {FFmpegLoader.ErrorText(received)}");
                break;
            }
            result = Convert(_frame, timestampUs);
            ffmpeg.av_frame_unref(_frame);
        }
        return result;
    }

    private DecodedFrame? Convert(AVFrame* frame, long timestampUs)
    {
        var source = frame;
        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            ffmpeg.av_frame_unref(_transfer);
            if (ffmpeg.av_hwframe_transfer_data(_transfer, frame, 0) < 0) return null;
            source = _transfer;
        }

        var width = source->width;
        var height = source->height;
        var format = (AVPixelFormat)source->format;
        if (_scaler is null || width != _scalerWidth || height != _scalerHeight || format != _scalerFormat)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = ffmpeg.sws_getContext(width, height, format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_POINT, null, null, null);
            var raw = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            var coefficients = new int_array4 { [0] = raw[0], [1] = raw[1], [2] = raw[2], [3] = raw[3] };
            ffmpeg.sws_setColorspaceDetails(_scaler, coefficients, 0, coefficients, 1, 0, 1 << 16, 1 << 16);
            _scalerWidth = width;
            _scalerHeight = height;
            _scalerFormat = format;
        }

        var stride = width * 4;
        _bufferIndex = (_bufferIndex + 1) % _buffers.Length;
        var buffer = _buffers[_bufferIndex];
        if (buffer is null || buffer.Width != width || buffer.Height != height)
            buffer = _buffers[_bufferIndex] = new DecodedFrame { Width = width, Height = height, Stride = stride, Pixels = GC.AllocateUninitializedArray<byte>(stride * height, pinned: true) };

        fixed (byte* destination = buffer.Pixels)
        {
            var planes = new byte_ptrArray4 { [0] = destination };
            var strides = new int_array4 { [0] = stride };
            ffmpeg.sws_scale(_scaler, source->data, source->linesize, 0, height, planes, strides);
        }
        buffer.TimestampUs = timestampUs;
        return buffer;
    }

    private void Free()
    {
        if (_context is not null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_hwDevice is not null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
    }

    public void Dispose()
    {
        Free();
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_transfer is not null) { var f = _transfer; ffmpeg.av_frame_free(&f); _transfer = null; }
        if (_scaler is not null) { ffmpeg.sws_freeContext(_scaler); _scaler = null; }
    }
}
