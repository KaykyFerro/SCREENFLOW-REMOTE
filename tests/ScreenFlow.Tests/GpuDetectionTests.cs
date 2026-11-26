using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Placa de vídeo (1.2.0): qual é a principal, a versão do driver e por que o encoder dela não abriu.</summary>
public class GpuDetectionTests
{
    private static readonly GpuInfo Rtx = new("NVIDIA GeForce RTX 2060", EncoderVendor.Nvidia, 6144) { Driver = "552.22" };
    private static readonly GpuInfo Intel = new("Intel(R) UHD Graphics 630", EncoderVendor.Intel, 128);
    private static readonly GpuInfo Parsec = new("Parsec Virtual Display Adapter", EncoderVendor.Software, 0);

    private static HardwareProfile Profile(IReadOnlyList<GpuInfo> gpus, IReadOnlyList<EncoderInfo> encoders, Dictionary<string, IReadOnlyList<string>>? failures = null)
        => new(gpus, encoders, [], 12, 32768, 2560, 1440, 165, "Windows 11") { EncoderFailures = failures ?? [] };

    [Fact]
    public void TheRealCardWinsOverVirtualAndIntegratedOnes()
    {
        Assert.Equal(Rtx, GpuInfo.Main([Parsec, Intel, Rtx]));
        // Uma placa virtual que diz ter mais memória não passa na frente da de verdade.
        Assert.Equal(Intel, GpuInfo.Main([Parsec with { DedicatedVideoMemoryMb = 16384 }, Intel]));
        Assert.Null(GpuInfo.Main([]));
    }

    [Fact]
    public void ACardWithAProblemStillShowsUpAsTheMainOne()
    {
        // O código 43 tira a placa do DXGI, mas o Windows ainda mostra o nome e a memória dela.
        var broken = Rtx with { Problem = "o Windows parou a placa porque ela relatou problemas (código 43)" };
        Assert.Equal(broken, GpuInfo.Main([Intel, broken]));
    }

    [Fact]
    public void ACardWithoutItsDriverStillBeatsTheIntegratedOne()
    {
        // Sem o driver da NVIDIA o Windows não sabe a memória da placa, mas é ela que a pessoa procura no cartão.
        var noDriver = new GpuInfo("Placa NVIDIA sem driver", EncoderVendor.Nvidia, 0) { Problem = "está sem o driver do fabricante" };
        Assert.Equal(noDriver, GpuInfo.Main([Intel, noDriver]));
        Assert.Equal("Placa NVIDIA sem driver", noDriver.ShortName);
        // Uma integrada desativada que não informa a memória não passa na frente da dedicada que funciona.
        var disabledIntel = Intel with { DedicatedVideoMemoryMb = 0, Problem = "desativada no Gerenciador de Dispositivos (código 22)" };
        Assert.Equal(Rtx, GpuInfo.Main([disabledIntel, Rtx]));
        Assert.Equal(Intel, GpuInfo.Main([disabledIntel, Intel]));
    }

    [Theory]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("31.0.101.4502", null)]  // versão da Intel: não tem esse formato
    [InlineData("texto", null)]
    [InlineData(null, null)]
    public void NvidiaDriverVersionComesFromTheWindowsOne(string? windows, string? expected)
        => Assert.Equal(expected, GpuInfo.NvidiaVersion(windows));

    [Fact]
    public void OldDriverIsExplainedWithTheMinimumVersion()
    {
        var hint = GpuInfo.EncoderHint(EncoderVendor.Nvidia,
        [
            "[hevc_nvenc @ 000001] Driver does not support the required nvenc API version. Required: 12.2 Found: 12.1",
            "[hevc_nvenc @ 000001] The minimum required Nvidia driver for nvenc is 551.76 or newer",
        ]);
        Assert.Equal("o driver da NVIDIA é antigo para o NVENC: atualize para o 551.76 ou mais novo", hint);
    }

    [Fact]
    public void BusyNvencAndUnknownMessagesAreToldApart()
    {
        Assert.Contains("ocupado", GpuInfo.EncoderHint(EncoderVendor.Nvidia, ["OpenEncodeSessionEx failed: out of memory (10): (no details)"]));
        Assert.Null(GpuInfo.EncoderHint(EncoderVendor.Nvidia, ["algo que ninguém conhece"]));
        Assert.Null(GpuInfo.EncoderHint(EncoderVendor.Nvidia, []));
    }

    [Fact]
    public void MainCardEncoderAndHintFollowTheProbe()
    {
        var nvenc = new EncoderInfo("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia, true, 8192, 8192);
        var qsv = new EncoderInfo("h264_qsv", VideoCodec.H264, EncoderVendor.Intel, true, 4096, 4096);
        var software = new EncoderInfo("libopenh264", VideoCodec.H264, EncoderVendor.Software, false, 4096, 4096);

        var working = Profile([Intel, Rtx], [nvenc, qsv, software]);
        Assert.Equal(Rtx, working.MainGpu);
        Assert.Equal(nvenc, working.MainGpuEncoder);
        Assert.Null(working.MainGpuEncoderHint);

        // Driver antigo: a RTX continua sendo a placa principal, sem encoder e com o motivo.
        var oldDriver = Profile([Intel, Rtx], [qsv, software], new()
        {
            ["hevc_nvenc"] = ["The minimum required Nvidia driver for nvenc is 551.76 or newer"],
            ["h264_nvenc"] = ["The minimum required Nvidia driver for nvenc is 551.76 or newer"],
        });
        Assert.Equal(Rtx, oldDriver.MainGpu);
        Assert.Null(oldDriver.MainGpuEncoder);
        Assert.Contains("551.76", oldDriver.MainGpuEncoderHint);
    }

    [Theory]
    [InlineData("hevc_nvenc", EncoderVendor.Nvidia)]
    [InlineData("av1_amf", EncoderVendor.Amd)]
    [InlineData("h264_qsv", EncoderVendor.Intel)]
    [InlineData("libopenh264", EncoderVendor.Software)]
    public void VendorComesFromTheEncoderName(string name, EncoderVendor vendor) => Assert.Equal(vendor, HardwareProfile.VendorOf(name));

    [Fact]
    public void ShortNameStillReadsLikeTheBox() => Assert.Equal("RTX 2060", Rtx.ShortName);
}
