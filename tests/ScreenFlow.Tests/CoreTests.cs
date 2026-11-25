using System.Net;
using ScreenFlow.Core;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;
using ScreenFlow.Core.Settings;
using ScreenFlow.Core.Updates;
using Xunit;

namespace ScreenFlow.Tests;

public class SecurityTests
{
    [Fact]
    public void KeyExchange_ProducesSameSecretOnBothSides()
    {
        using var host = new KeyExchange();
        using var viewer = new KeyExchange();
        Assert.Equal(host.DeriveSharedSecret(viewer.PublicKey), viewer.DeriveSharedSecret(host.PublicKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1234")]
    public void Proof_VerifiesWithRightPinOnly(string? pin)
    {
        using var host = new KeyExchange();
        using var viewer = new KeyExchange();
        var nonce = SessionCrypto.CreateNonce();
        var transcript = SessionCrypto.Transcript(viewer.PublicKey, host.PublicKey, nonce);
        var proof = SessionCrypto.CreateProof(viewer.DeriveSharedSecret(host.PublicKey), pin, transcript);
        var secret = host.DeriveSharedSecret(viewer.PublicKey);
        Assert.True(SessionCrypto.VerifyProof(secret, pin, transcript, proof));
        Assert.False(SessionCrypto.VerifyProof(secret, "9999", transcript, proof));
        Assert.False(SessionCrypto.VerifyProof(secret, pin, transcript, "não-é-base64"));
    }

    [Fact]
    public void PacketCipher_RoundTripsAndDetectsTampering()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var salt = new byte[] { 1, 2, 3, 4 };
        using var sender = new PacketCipher(key, salt);
        using var receiver = new PacketCipher(key, salt);
        var header = new byte[MediaHeader.Size];
        var plain = "frame de vídeo"u8.ToArray();
        var cipher = new byte[plain.Length + PacketCipher.TagSize];
        sender.Encrypt(77, header, plain, cipher);

        var output = new byte[plain.Length];
        Assert.True(receiver.TryDecrypt(77, header, cipher, output));
        Assert.Equal(plain, output);
        Assert.False(receiver.TryDecrypt(78, header, cipher, output));
        header[5] ^= 1;
        Assert.False(receiver.TryDecrypt(77, header, cipher, output));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("123", false)]
    [InlineData("1234", true)]
    public void PinValidation(string? pin, bool valid) => Assert.Equal(valid, SessionCrypto.IsValidPin(pin));
}

public class NetworkTests
{
    [Theory]
    [InlineData("26.10.20.30", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("25.1.1.1", false)]
    public void RadminRange(string ip, bool expected) => Assert.Equal(expected, RadminAddress.IsRadmin(IPAddress.Parse(ip)));

    [Fact]
    public void Broadcast_ForRadminSubnet()
        => Assert.Equal(IPAddress.Parse("26.255.255.255"), RadminAddress.BroadcastFor(IPAddress.Parse("26.4.5.6"), IPAddress.Parse("255.0.0.0")));

    [Theory]
    [InlineData("26.1.2.3", true)]
    [InlineData(" 26.1.2.3:47801 ", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void ParseHost(string input, bool ok) => Assert.Equal(ok, RadminAddress.TryParseHost(input, out _));

    [Fact]
    public void Backoff_GrowsAndCaps()
    {
        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), seed: 1);
        var first = backoff.Next();
        TimeSpan last = default;
        for (var i = 0; i < 20; i++) last = backoff.Next();
        Assert.InRange(first.TotalMilliseconds, 80, 120);
        Assert.InRange(last.TotalMilliseconds, 1000, 2000);
        backoff.Reset();
        Assert.Equal(0, backoff.Attempt);
    }
}

public class QualityTests
{
    [Fact]
    public void Bitrates_MatchProjectProfiles()
    {
        Assert.InRange(Quality.TargetBitrateKbps(Resolution.P1080, 60, VideoCodec.Hevc), 10_000, 25_000);
        Assert.InRange(Quality.TargetBitrateKbps(Resolution.P1440, 144, VideoCodec.Hevc), 40_000, 60_000);
        Assert.InRange(Quality.TargetBitrateKbps(Resolution.P2160, 60, VideoCodec.Hevc), 40_000, 60_000);
        Assert.InRange(Quality.TargetBitrateKbps(Resolution.P2160, 165, VideoCodec.Hevc), 90_000, 150_000);
        Assert.True(Quality.TargetBitrateKbps(Resolution.P1080, 60, VideoCodec.H264) > Quality.TargetBitrateKbps(Resolution.P1080, 60, VideoCodec.Hevc));
    }

    [Fact]
    public void Codec_PrefersAv1ThenHevcThenH264()
    {
        Assert.Equal(VideoCodec.Av1, CodecNegotiation.Choose([VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1], [VideoCodec.Av1, VideoCodec.H264]));
        Assert.Equal(VideoCodec.Hevc, CodecNegotiation.Choose([VideoCodec.H264, VideoCodec.Hevc], [VideoCodec.Av1, VideoCodec.Hevc, VideoCodec.H264]));
        Assert.Equal(VideoCodec.H264, CodecNegotiation.Choose([VideoCodec.H264], [VideoCodec.H264]));
        Assert.Null(CodecNegotiation.Choose([VideoCodec.Av1], [VideoCodec.H264]));
    }

    [Fact]
    public void FitTo_KeepsAspectAndEvenSize()
    {
        Assert.Equal((1920, 1080), Resolution.P1080.FitTo(3840, 2160));
        Assert.Equal((1280, 1080), Resolution.P1080.FitTo(1280, 1080));
        var (w, h) = Resolution.P1080.FitTo(3440, 1440);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(w <= 1920 && h <= 1080);
    }

    [Fact]
    public void Adaptive_ReducesBitrateThenFpsThenResolution()
    {
        var profile = new StreamTarget(Quality.TargetBitrateKbps(Resolution.P2160, 165, VideoCodec.Hevc), 165, Resolution.P2160);
        var controller = new AdaptiveBitrateController(profile, VideoCodec.Hevc);
        var bad = new NetworkSample(0.08, 20, 50_000, 165);

        var steps = new List<StreamTarget>();
        for (var i = 0; i < 30; i++)
            if (controller.Update(bad) is { } next) steps.Add(next);

        var firstFpsDrop = steps.FindIndex(s => s.Fps < 165);
        var firstResDrop = steps.FindIndex(s => s.Resolution != Resolution.P2160);
        Assert.True(firstFpsDrop > 0, "bitrate deve cair antes do FPS");
        Assert.True(firstResDrop > firstFpsDrop, "FPS deve cair antes da resolução");
        Assert.All(steps.Take(firstFpsDrop), s => Assert.Equal(165, s.Fps));
    }

    [Fact]
    public void Adaptive_RecoversToProfileWhenStable()
    {
        var profile = new StreamTarget(20_000, 60, Resolution.P1080);
        var controller = new AdaptiveBitrateController(profile, VideoCodec.Hevc);
        for (var i = 0; i < 10; i++) controller.Update(new NetworkSample(0.1, 20, 5_000, 60));
        Assert.NotEqual(profile, controller.Current);
        for (var i = 0; i < 500; i++) controller.Update(new NetworkSample(0, 20, 20_000, 60));
        Assert.Equal(profile, controller.Current);
    }
}

public class SettingsTests
{
    private static HardwareProfile Hardware(EncoderInfo[] encoders, int w = 3840, int h = 2160, int hz = 165, string gpu = "NVIDIA GeForce RTX 4080")
        => new([new GpuInfo(gpu, EncoderVendor.Nvidia, 16000)], encoders, [VideoCodec.Hevc], 16, 32000, w, h, hz, "Windows 11");

    [Fact]
    public void AutoConfig_PicksBestHardwareProfile()
    {
        var hw = Hardware([
            new EncoderInfo("h264_nvenc", VideoCodec.H264, EncoderVendor.Nvidia, true, 4096, 4096),
            new EncoderInfo("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia, true, 8192, 8192),
            new EncoderInfo("av1_nvenc", VideoCodec.Av1, EncoderVendor.Nvidia, true, 8192, 8192),
            new EncoderInfo("libx264", VideoCodec.H264, EncoderVendor.Software, false, 4096, 4096)]);
        var result = AutoConfigurator.Recommend(hw, new NetworkProfile("26.1.1.1", 0, null));
        Assert.Equal("av1_nvenc", result.Encoder);
        Assert.Equal(Resolution.P2160, result.Resolution);
        Assert.Equal(165, result.Fps);
    }

    [Fact]
    public void AutoConfig_SoftwareFallbackIsLimitedAndWarned()
    {
        var result = AutoConfigurator.Recommend(Hardware([new EncoderInfo("libx264", VideoCodec.H264, EncoderVendor.Software, false, 4096, 4096)]), new NetworkProfile(null, 0, null));
        Assert.True(result.Resolution.Pixels <= Resolution.P1080.Pixels);
        Assert.True(result.Fps <= 60);
        Assert.Contains(result.Notes, n => n.Contains("165"));
        Assert.Contains(result.Notes, n => n.Contains("Radmin"));
    }

    [Fact]
    public void AutoConfig_RespectsMeasuredBandwidth()
    {
        var hw = Hardware([new EncoderInfo("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia, true, 8192, 8192)]);
        var result = AutoConfigurator.Recommend(hw, new NetworkProfile("26.1.1.1", 1000, 40));
        Assert.True(result.BitrateKbps <= 28_000);
        Assert.True(result.Fps <= 60 || result.Resolution != Resolution.P2160);
    }

    [Fact]
    public void AutoConfig_RespectsMonitorRefresh()
    {
        var hw = Hardware([new EncoderInfo("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia, true, 8192, 8192)], 1920, 1080, 144);
        var result = AutoConfigurator.Recommend(hw, new NetworkProfile("26.1.1.1", 0, null));
        Assert.Equal(Resolution.P1080, result.Resolution);
        Assert.Equal(144, result.Fps);
    }

    [Fact]
    public void Settings_PersistAndNormalize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.True(store.IsFirstRun);
            store.Current.Fps = 999;
            store.Current.Pin = "12";
            store.Current.RememberHost("26.0.0.1");
            store.Current.RememberHost("26.0.0.2");
            store.Current.RememberHost("26.0.0.1");
            store.Save();

            var reloaded = new SettingsStore(path);
            Assert.False(reloaded.IsFirstRun);
            Assert.Equal(60, reloaded.Current.Fps);
            Assert.Null(reloaded.Current.Pin);
            Assert.Equal(["26.0.0.1", "26.0.0.2"], reloaded.Current.RecentHosts);
        }
        finally { File.Delete(path); }
    }
}

public class UpdateTests
{
    private const string Json = """
    {"tag_name":"v1.2.3","name":"ScreenFlow 1.2.3","body":"notas","draft":false,"prerelease":false,
     "assets":[{"name":"ScreenFlow-Setup-win-x64.exe","browser_download_url":"https://example/a.exe","size":123},
               {"name":"ScreenFlow-Setup-win-x64.exe.sha256","browser_download_url":"https://example/a.sha256","size":64}]}
    """;

    [Fact]
    public void ParsesLatestRelease()
    {
        var release = ReleaseParser.Parse(Json)!;
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal("https://example/a.exe", release.InstallerUrl);
        Assert.Equal("https://example/a.sha256", release.Sha256Url);
        Assert.True(ReleaseParser.IsNewer(release, new Version(1, 2, 2, 0)));
        Assert.False(ReleaseParser.IsNewer(release, new Version(1, 2, 3, 0)));
    }

    [Theory]
    [InlineData("v2.0.1", true)]
    [InlineData("1.4", true)]
    [InlineData("v1.0.0-beta", true)]
    [InlineData("nightly", false)]
    public void ParsesTags(string tag, bool ok) => Assert.Equal(ok, ReleaseParser.TryParseVersion(tag, out _));
}

public class ClockTests
{
    [Fact]
    public void ClockSync_EstimatesOffset()
    {
        var sync = new ClockSync();
        // host está 5 s à frente; RTT 10 ms
        sync.AddSample(1_000_000, 6_005_000, 1_010_000);
        Assert.True(sync.HasEstimate);
        Assert.InRange(sync.HostToViewerUs(6_005_000), 1_004_000, 1_006_000);
        Assert.InRange(sync.RoundTripMs, 9, 11);
    }

    [Fact]
    public void RateMeter_CountsLastSecond()
    {
        var meter = new RateMeter();
        meter.Add(10, 0);
        meter.Add(20, 500_000);
        Assert.Equal(30, meter.PerSecond(900_000));
        Assert.Equal(20, meter.PerSecond(1_200_000));
    }

    [Fact]
    public void AppInfo_HasDefaults()
    {
        Assert.Equal("ScreenFlow", AppInfo.Name);
        Assert.Equal(47800, AppInfo.DefaultMediaPort);
        Assert.Equal(47801, AppInfo.DefaultControlPort);
    }
}

public class MultiViewerTests
{
    [Fact]
    public void UploadIsSplitBetweenViewers()
    {
        Assert.Equal(120_000, Quality.PerViewerBitrateKbps(120_000, 0, 10));
        Assert.Equal(8_000, Quality.PerViewerBitrateKbps(120_000, 100, 10));
        Assert.Equal(120_000, Quality.PerViewerBitrateKbps(120_000, 1000, 1));
        Assert.Equal(1_000, Quality.PerViewerBitrateKbps(120_000, 5, 50));
    }
}
