using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Win32;
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
    private static IReadOnlyList<GpuInfo>? _gpus;

    public static HardwareProfile Detect(bool refresh = false)
    {
        if (_cached is not null && !refresh) return _cached;
        var gpus = Gpus(refresh);
        var (encoders, failures) = DetectEncoders(gpus);
        var decoders = DetectHardwareDecoders();
        var monitors = MonitorDiscovery.GetMonitors();
        var best = monitors.OrderByDescending(m => m.IsPrimary).FirstOrDefault();
        var memoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        _cached = new HardwareProfile(gpus, encoders, decoders, Environment.ProcessorCount, memoryMb,
            best?.Width ?? 1920, best?.Height ?? 1080, monitors.Count == 0 ? 60 : monitors.Max(m => m.RefreshRateHz),
            Environment.OSVersion.VersionString) { EncoderFailures = failures };
        Log.Info($"Hardware: GPUs=[{string.Join(", ", gpus.Select(Describe))}] encoders=[{string.Join(", ", encoders.Select(e => e.FfmpegName))}] decodersHW=[{string.Join(", ", decoders)}] CPU={Environment.ProcessorCount} RAM={memoryMb} MB");
        foreach (var (name, messages) in failures)
            Log.Info($"Encoder {name} não abriu: {(messages.Count == 0 ? "sem mensagem do FFmpeg" : string.Join(" | ", messages))}");
        return _cached;
    }

    /// <summary>Só as placas de vídeo: rápido (não abre encoders), para o cartão da placa aparecer logo.</summary>
    public static IReadOnlyList<GpuInfo> Gpus(bool refresh = false)
    {
        lock (GpuGate)
        {
            if (!refresh && _gpus is { } known) return known;
            return _gpus = DetectGpus();
        }
    }

    private static readonly object GpuGate = new();

    private static string Describe(GpuInfo gpu)
        => $"{gpu.Name} ({gpu.Vendor}, {gpu.DedicatedVideoMemoryMb} MB{(gpu.Driver is null ? string.Empty : $", driver {gpu.Driver}")}{(gpu.Problem is null ? string.Empty : $", {gpu.Problem}")})";

    /// <summary>
    /// As placas de vídeo pelo DXGI (cada uma lida à parte: uma placa virtual que falha não esconde as outras) e, para as
    /// que o DXGI não mostra, pela lista de dispositivos do Windows: uma placa com erro no driver (o famoso código 43) ou
    /// desativada não aparece no DXGI, mas aparece lá com o motivo.
    /// </summary>
    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        var list = new List<GpuInfo>();
        var seen = new HashSet<(uint Vendor, uint Device)>();
        var devices = DisplayDevices();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; i < 16; i++)
            {
                var result = factory.EnumAdapters1(i, out var adapter);
                if (result.Code == Vortice.DXGI.ResultCode.NotFound.Code) break;
                if (result.Failure || adapter is null)
                {
                    // Um adaptador que não abre não para a lista: o próximo pode ser a placa de verdade.
                    Log.Warn($"DXGI: o adaptador {i} não abriu ({result}).");
                    adapter?.Dispose();
                    continue;
                }
                using (adapter)
                {
                    try
                    {
                        var desc = adapter.Description1;
                        var name = desc.Description.TrimEnd('\0').Trim();
                        // O renderizador por software (WARP) não é placa de vídeo.
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue;
                        var device = devices.FirstOrDefault(d => d.VendorId == desc.VendorId && d.DeviceId == desc.DeviceId);
                        seen.Add((desc.VendorId, desc.DeviceId));
                        var vendor = desc.VendorId == MicrosoftVendor ? EncoderVendor.Software : VendorFromId(desc.VendorId);
                        var basic = vendor != EncoderVendor.Software && (IsBasicName(name) || device?.BasicDriver == true);
                        list.Add(new GpuInfo(basic ? NoDriverName(desc.VendorId) : name.Length > 0 ? name : device?.Name ?? "Placa de vídeo", vendor, (long)desc.DedicatedVideoMemory / (1024 * 1024))
                        {
                            Driver = basic ? null : DriverLabel(desc.VendorId, UserModeDriverVersion(adapter) ?? device?.DriverVersion),
                            Problem = device?.Problem ?? (basic ? NoVendorDriver : null),
                        });
                    }
                    catch (Exception exception) { Log.Warn($"DXGI: não deu para ler o adaptador {i}: {exception.Message}"); }
                }
            }
        }
        catch (Exception exception) { Log.Warn($"Falha ao listar GPUs pelo DXGI: {exception.Message}"); }

        foreach (var device in devices)
        {
            if (device.VendorId == MicrosoftVendor) continue;
            var vendor = VendorFromId(device.VendorId);
            // Sem o driver do fabricante a placa se chama "Vídeo Básico da Microsoft", igual à entrada do DXGI: o nome não conta.
            var basic = device.BasicDriver && vendor != EncoderVendor.Software;
            var listed = seen.Contains((device.VendorId, device.DeviceId))
                         || (!basic && list.Any(g => g.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase)));
            if (listed) continue;
            // Uma placa que o DXGI não mostrou: com problema no Windows, sem o driver do fabricante, ou o DXGI não respondeu.
            if (device.Problem is null && !basic && list.Any(g => g.Vendor != EncoderVendor.Software)) continue;
            list.Add(new GpuInfo(basic ? NoDriverName(device.VendorId) : device.Name, vendor, device.MemoryMb)
            {
                Driver = basic ? null : DriverLabel(device.VendorId, device.DriverVersion),
                Problem = device.Problem ?? (basic ? NoVendorDriver : null),
            });
        }
        return list;
    }

    private const string NoVendorDriver = "está sem o driver do fabricante (o Windows usa o vídeo básico): instale o driver da placa";

    /// <summary>Sem o driver do fabricante, o Windows mostra a placa como "Vídeo Básico da Microsoft" (em inglês ou em português).</summary>
    private static bool IsBasicName(string name)
        => name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Vídeo Básico", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Video Básico", StringComparison.OrdinalIgnoreCase) || name.Contains("Video Basico", StringComparison.OrdinalIgnoreCase);

    /// <summary>O nome para uma placa sem driver (o Windows não sabe o modelo): "Placa NVIDIA sem driver".</summary>
    private static string NoDriverName(uint vendorId) => VendorFromId(vendorId) switch
    {
        EncoderVendor.Nvidia => "Placa NVIDIA sem driver",
        EncoderVendor.Amd => "Placa AMD sem driver",
        EncoderVendor.Intel => "Placa Intel sem driver",
        _ => "Placa de vídeo sem driver",
    };

    private const uint MicrosoftVendor = 0x1414;

    private static string? DriverLabel(uint vendorId, string? windowsVersion)
        => windowsVersion is null ? null : vendorId == 0x10DE ? GpuInfo.NvidiaVersion(windowsVersion) ?? windowsVersion : windowsVersion;

    /// <summary>"31.0.15.5222": a versão do driver de vídeo, pelo DXGI.</summary>
    private static string? UserModeDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), out long version).Failure) return null;
            return $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";
        }
        catch (Exception) { return null; }
    }

    /// <summary>Uma placa de vídeo na lista de dispositivos do Windows.</summary>
    private sealed record DisplayDevice(string Name, uint VendorId, uint DeviceId, string? DriverVersion, long MemoryMb, string? Problem, bool BasicDriver);

    /// <summary>As placas de vídeo presentes no Windows (Gerenciador de Dispositivos), com o problema de cada uma.</summary>
    private static IReadOnlyList<DisplayDevice> DisplayDevices()
    {
        var list = new List<DisplayDevice>();
        var displayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        var set = SetupDiGetClassDevsW(ref displayClass, null, 0, DigcfPresent);
        if (set == -1 || set == 0) return list;
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                try
                {
                    var name = Property(set, ref data, SpdrpFriendlyName) ?? Property(set, ref data, SpdrpDeviceDesc) ?? "Placa de vídeo";
                    var hardwareId = Property(set, ref data, SpdrpHardwareId) ?? string.Empty;
                    var vendor = HexAfter(hardwareId, "VEN_");
                    var device = HexAfter(hardwareId, "DEV_");
                    // O serviço "BasicDisplay" é o vídeo básico da Microsoft: a placa está sem o driver do fabricante (vale em qualquer idioma).
                    var basicDriver = string.Equals(Property(set, ref data, SpdrpService), "BasicDisplay", StringComparison.OrdinalIgnoreCase) || IsBasicName(name);
                    string? driverVersion = null;
                    long memoryMb = 0;
                    if (Property(set, ref data, SpdrpDriver) is { Length: > 0 } driverKey)
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                        driverVersion = key?.GetValue("DriverVersion") as string;
                        memoryMb = key?.GetValue("HardwareInformation.qwMemorySize") switch
                        {
                            long bytes => bytes / (1024 * 1024),
                            byte[] { Length: >= 8 } raw => BitConverter.ToInt64(raw, 0) / (1024 * 1024),
                            int bytes => (uint)bytes / (1024 * 1024),
                            byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0) / (1024 * 1024),
                            _ => key?.GetValue("HardwareInformation.MemorySize") switch
                            {
                                int bytes => (uint)bytes / (1024 * 1024),
                                byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0) / (1024 * 1024),
                                _ => 0,
                            },
                        };
                    }
                    list.Add(new DisplayDevice(name.Trim(), vendor, device, driverVersion, memoryMb, ProblemOf(data.DevInst), basicDriver));
                }
                catch (Exception exception) { Log.Debug($"Dispositivo de vídeo {i} não deu para ler: {exception.Message}"); }
            }
        }
        catch (Exception exception) { Log.Warn($"Falha ao ler as placas de vídeo do Windows: {exception.Message}"); }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>O problema que o Gerenciador de Dispositivos mostra (o código e o que ele quer dizer), ou null.</summary>
    private static string? ProblemOf(uint devInst)
    {
        try
        {
            if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != 0 || (status & DnHasProblem) == 0) return null;
            return problem switch
            {
                14 => "o Windows precisa reiniciar para a placa funcionar (código 14)",
                22 => "desativada no Gerenciador de Dispositivos (código 22)",
                28 => "sem driver instalado (código 28)",
                31 or 39 => $"o driver não carregou (código {problem})",
                43 => "o Windows parou a placa porque ela relatou problemas (código 43)",
                _ => $"com problema no Gerenciador de Dispositivos (código {problem})",
            };
        }
        catch (Exception) { return null; }
    }

    private static uint HexAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + marker.Length + 4 > text.Length) return 0;
        return uint.TryParse(text.AsSpan(index + marker.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : 0;
    }

    private static string? Property(nint set, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size) || size < 2) return null;
        // REG_SZ ou REG_MULTI_SZ: o primeiro texto basta.
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim() is { Length: > 0 } value ? value : null;
    }

    private const uint DigcfPresent = 0x2, DnHasProblem = 0x400;
    private const uint SpdrpDeviceDesc = 0x0, SpdrpHardwareId = 0x1, SpdrpService = 0x4, SpdrpDriver = 0x9, SpdrpFriendlyName = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SpDevInfoData data, uint property, out uint type, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    public static EncoderVendor VendorFromId(uint vendorId) => vendorId switch
    {
        0x10DE => EncoderVendor.Nvidia,
        0x1002 or 0x1022 => EncoderVendor.Amd,
        0x8086 => EncoderVendor.Intel,
        _ => EncoderVendor.Software,
    };

    public static IReadOnlyList<EncoderInfo> DetectEncoders() => DetectEncoders(Gpus()).Encoders;

    /// <summary>
    /// Abre cada encoder num frame pequeno. Só tenta os da marca das placas que funcionam (uma NVIDIA sozinha não
    /// precisa esperar o AMF e o Quick Sync falharem); se nenhuma placa for achada, tenta todos. As mensagens do FFmpeg
    /// de quem não abriu ficam guardadas: dizem se o driver é antigo ou se o NVENC está ocupado.
    /// </summary>
    private static (IReadOnlyList<EncoderInfo> Encoders, IReadOnlyDictionary<string, IReadOnlyList<string>> Failures) DetectEncoders(IReadOnlyList<GpuInfo> gpus)
    {
        var list = new List<EncoderInfo>();
        var failures = new Dictionary<string, IReadOnlyList<string>>();
        if (!FFmpegLoader.EnsureLoaded()) return (list, failures);
        // Uma placa com problema também entra: o Windows pode apontar algo (reiniciar depois do driver novo) com o encoder funcionando.
        var vendors = gpus.Where(g => g.Vendor != EncoderVendor.Software).Select(g => g.Vendor).ToHashSet();
        foreach (var (name, codec, vendor) in Candidates)
        {
            if (vendor == EncoderVendor.Software && list.Any(e => !e.Hardware)) continue; // um software basta
            if (vendor != EncoderVendor.Software && vendors.Count > 0 && !vendors.Contains(vendor)) continue;
            var opened = FFmpegLog.Capture(() => TryOpenEncoder(name), out var messages);
            if (opened)
            {
                var max = codec == VideoCodec.H264 ? 4096 : 8192;
                list.Add(new EncoderInfo(name, codec, vendor, vendor != EncoderVendor.Software, max, max));
            }
            else if (vendor != EncoderVendor.Software && ffmpeg.avcodec_find_encoder_by_name(name) is not null) failures[name] = messages;
        }
        return (list, failures);
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
