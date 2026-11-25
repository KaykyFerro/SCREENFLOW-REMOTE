using FFmpeg.AutoGen;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;
using ScreenFlow.Infrastructure.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenFlow.Infrastructure.Hardware;

/// <summary>Detecta GPU, encoders/decoders por hardware, CPU, memória e monitores para a configuração automática.</summary>
public static unsafe class HardwareDetector
{
    private static readonly (string Name, VideoCodec Codec, EncoderVendor Vendor)[] Candidates =
    [
        ("av1_nvenc", VideoCodec.Av1, EncoderVendor.Nvidia),
        ("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia),
        ("h264_nvenc", VideoCodec.H264, EncoderVendor.Nvidia),
        ("av1_amf", VideoCodec.Av1, EncoderVendor.Amd),
        ("hevc_amf", VideoCodec.Hevc, EncoderVendor.Amd),
        ("h264_amf", VideoCodec.H264, EncoderVendor.Amd),
        ("av1_qsv", VideoCodec.Av1, EncoderVendor.Intel),
        ("hevc_qsv", VideoCodec.Hevc, EncoderVendor.Intel),
        ("h264_qsv", VideoCodec.H264, EncoderVendor.Intel),
        ("libx264", VideoCodec.H264, EncoderVendor.Software),      // só existe em builds GPL do FFmpeg
        ("libopenh264", VideoCodec.H264, EncoderVendor.Software),  // reserva por software na build LGPL
    ];

    // GUIDs de perfis D3D11 de decodificação (d3d11.h / dxva.h)
    private static readonly Guid H264Vld = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    private static readonly Guid HevcMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    private static readonly Guid Av1Profile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");

    private static HardwareProfile? _cached;

    public static HardwareProfile Detect(bool refresh = false)
    {
        if (_cached is not null && !refresh) return _cached;
        var gpus = DetectGpus();
        var encoders = DetectEncoders();
        var decoders = DetectHardwareDecoders();
        var monitors = MonitorDiscovery.GetMonitors();
        var best = monitors.OrderByDescending(m => m.IsPrimary).FirstOrDefault();
        var memoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        _cached = new HardwareProfile(gpus, encoders, decoders, Environment.ProcessorCount, memoryMb,
            best?.Width ?? 1920, best?.Height ?? 1080, monitors.Count == 0 ? 60 : monitors.Max(m => m.RefreshRateHz),
            Environment.OSVersion.VersionString);
        Log.Info($"Hardware: GPUs=[{string.Join(", ", gpus.Select(g => g.Name))}] encoders=[{string.Join(", ", encoders.Select(e => e.FfmpegName))}] decodersHW=[{string.Join(", ", decoders)}] CPU={Environment.ProcessorCount} RAM={memoryMb} MB");
        return _cached;
    }

    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0) continue;
                    list.Add(new GpuInfo(desc.Description.TrimEnd('\0'), VendorFromId(desc.VendorId), (long)desc.DedicatedVideoMemory / (1024 * 1024)));
                }
            }
        }
        catch (Exception exception) { Log.Warn($"Falha ao listar GPUs: {exception.Message}"); }
        return list;
    }

    public static EncoderVendor VendorFromId(uint vendorId) => vendorId switch
    {
        0x10DE => EncoderVendor.Nvidia,
        0x1002 or 0x1022 => EncoderVendor.Amd,
        0x8086 => EncoderVendor.Intel,
        _ => EncoderVendor.Software,
    };

    public static IReadOnlyList<EncoderInfo> DetectEncoders()
    {
        var list = new List<EncoderInfo>();
        if (!FFmpegLoader.EnsureLoaded()) return list;
        foreach (var (name, codec, vendor) in Candidates)
        {
            if (vendor == EncoderVendor.Software && list.Any(e => !e.Hardware)) continue; // um software basta
            if (TryOpenEncoder(name))
            {
                var max = codec == VideoCodec.H264 ? 4096 : 8192;
                list.Add(new EncoderInfo(name, codec, vendor, vendor != EncoderVendor.Software, max, max));
            }
        }
        return list;
    }

    /// <summary>Abre o encoder de verdade num frame pequeno: só funciona se a GPU e o driver suportarem.</summary>
    private static bool TryOpenEncoder(string name)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec is null) return false;
        var context = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            context->width = 1280;
            context->height = 720;
            context->time_base = new AVRational { num = 1, den = 60 };
            context->framerate = new AVRational { num = 60, den = 1 };
            context->pix_fmt = Media.VideoEncoder.CpuPixelFormat(name);
            context->bit_rate = 5_000_000;
            context->max_b_frames = 0;
            return ffmpeg.avcodec_open2(context, codec, null) >= 0;
        }
        catch { return false; }
        finally { ffmpeg.avcodec_free_context(&context); }
    }

    public static IReadOnlyList<VideoCodec> DetectHardwareDecoders()
    {
        var result = new List<VideoCodec>();
        try
        {
            var result1 = D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.VideoSupport,
                new[] { FeatureLevel.Level_11_0 }, out Vortice.Direct3D11.ID3D11Device? device, out Vortice.Direct3D11.ID3D11DeviceContext? context);
            if (result1.Failure || device is null) return result;
            using (context)
            using (device)
            using (var video = device.QueryInterfaceOrNull<Vortice.Direct3D11.ID3D11VideoDevice>())
            {
                if (video is null) return result;
                var count = video.VideoDecoderProfileCount;
                for (uint i = 0; i < count; i++)
                {
                    var profile = video.GetVideoDecoderProfile(i);
                    if (profile == H264Vld && !result.Contains(VideoCodec.H264)) result.Add(VideoCodec.H264);
                    if (profile == HevcMain && !result.Contains(VideoCodec.Hevc)) result.Add(VideoCodec.Hevc);
                    if (profile == Av1Profile0 && !result.Contains(VideoCodec.Av1)) result.Add(VideoCodec.Av1);
                }
            }
        }
        catch (Exception exception) { Log.Warn($"Falha ao consultar decoders D3D11: {exception.Message}"); }
        return result;
    }

    private static VideoCodec[]? _supportedDecoders;

    /// <summary>Codecs que este PC consegue decodificar (hardware ou software), para anunciar no handshake. Calculado uma vez.</summary>
    public static VideoCodec[] SupportedDecoders() => _supportedDecoders ??= DetectSupportedDecoders();

    private static VideoCodec[] DetectSupportedDecoders()
    {
        if (!FFmpegLoader.EnsureLoaded()) return [];
        var list = new List<VideoCodec>();
        if (ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AV1) is not null) list.Add(VideoCodec.Av1);
        if (ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC) is not null) list.Add(VideoCodec.Hevc);
        if (ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) is not null) list.Add(VideoCodec.H264);
        // AV1 por software a 4K/165 não roda: só anuncia AV1 se houver decoder por hardware.
        var hardware = DetectHardwareDecoders();
        if (!hardware.Contains(VideoCodec.Av1)) list.Remove(VideoCodec.Av1);
        return list.ToArray();
    }
}
