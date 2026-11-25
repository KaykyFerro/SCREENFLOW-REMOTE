using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Modos novos (câmera, festa de música) e compatibilidade com a 1.0.0.</summary>
public class ModeTests
{
    [Fact]
    public void StreamConfigFromOldHost_DefaultsToScreenWithVideo()
    {
        // Mensagem exatamente como a 1.0.0 envia (sem videoEnabled nem mode).
        const string json = """{"t":"config","codec":"Hevc","width":1920,"height":1080,"fps":60,"bitrateKbps":20000,"audioEnabled":true,"audioSampleRate":48000,"audioChannels":2,"encoderName":"hevc_nvenc","reason":"x"}""";
        var config = Assert.IsType<StreamConfigMessage>(ControlFraming.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.True(config.VideoEnabled);
        Assert.Equal(StreamMode.Screen, config.Mode);
        Assert.Equal(VideoCodec.Hevc, config.Codec);
    }

    [Fact]
    public void HelloFromOldViewer_HasNoFeatures()
    {
        const string json = """{"t":"hello","protocol":1,"appVersion":"1.0.0","viewerName":"PC","decoders":["H264"],"maxWidth":1920,"maxHeight":1080,"maxFps":60,"udpPort":5000,"publicKey":"k"}""";
        var hello = Assert.IsType<HelloMessage>(ControlFraming.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Null(hello.Features);
        Assert.False(hello.Supports(StreamModes.FeatureAudioOnly));
    }

    [Fact]
    public void HelloWithFeatures_RoundTrips()
    {
        var hello = new HelloMessage(AppInfo.ProtocolVersion, "1.0.1", "PC", [VideoCodec.H264], 1920, 1080, 60, 5000, "k", [.. StreamModes.ViewerFeatures]);
        var parsed = Assert.IsType<HelloMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(hello).AsSpan(4)));
        Assert.True(parsed.Supports(StreamModes.FeatureAudioOnly));
        Assert.True(parsed.Supports("CAMERA"));
        Assert.False(parsed.Supports("outra-coisa"));
    }

    [Fact]
    public void AudioOnlyConfig_RoundTrips()
    {
        var config = new StreamConfigMessage(VideoCodec.H264, 0, 0, 0, 0, true, 48000, 2, "", "festa", VideoEnabled: false, Mode: StreamMode.AudioOnly);
        var parsed = Assert.IsType<StreamConfigMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(config).AsSpan(4)));
        Assert.False(parsed.VideoEnabled);
        Assert.Equal(StreamMode.AudioOnly, parsed.Mode);
    }

    [Fact]
    public void DiscoveryFromOldHost_IsScreenMode()
    {
        var json = """{"app":"ScreenFlow","protocol":1,"hostName":"PC","address":"26.1.2.3","controlPort":47801,"version":"1.0.0","pinRequired":false,"viewers":0}"""u8;
        Assert.True(DiscoveryAnnouncement.TryParse(json, out var parsed));
        Assert.Equal(StreamMode.Screen, parsed!.Mode);

        var party = new DiscoveryAnnouncement("ScreenFlow", 1, "PC", "26.1.2.3", 47801, "1.0.1", false, 3, StreamMode.AudioOnly);
        Assert.True(DiscoveryAnnouncement.TryParse(party.ToBytes(), out var round));
        Assert.Equal(StreamMode.AudioOnly, round!.Mode);
    }

    [Fact]
    public void AudioPair_PacksCurrentAndPrevious()
    {
        byte[] current = [1, 2, 3, 4];
        byte[] previous = [9, 8];
        var payload = AudioPair.Pack(current, previous);
        Assert.True(AudioPair.TryUnpack(payload, out var c, out var p));
        Assert.Equal(current, c.ToArray());
        Assert.Equal(previous, p.ToArray());

        Assert.True(AudioPair.TryUnpack(AudioPair.Pack(current, []), out _, out var none));
        Assert.True(none.IsEmpty);
        Assert.False(AudioPair.TryUnpack([0], out _, out _));
        Assert.False(AudioPair.TryUnpack([0, 10, 1], out _, out _));
    }

    [Fact]
    public void HeaderAcceptsAudioPairType()
    {
        var header = new MediaHeader { Type = MediaPacketType.AudioPair, FrameId = 7, Count = 1 };
        var buffer = new byte[MediaHeader.Size];
        header.Write(buffer);
        Assert.True(MediaHeader.TryRead(buffer, out var read));
        Assert.Equal(MediaPacketType.AudioPair, read.Type);
        buffer[3] = 6;
        Assert.False(MediaHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void AudioProfiles_PerMode()
    {
        var music = AudioProfile.ForMode(StreamMode.AudioOnly, 160, 256);
        Assert.True(music.Music);
        Assert.True(music.Redundant);
        Assert.Equal(20, music.FrameMs);
        Assert.Equal(960, music.FrameSamples);
        Assert.Equal("audio", music.OpusApplication);
        Assert.Equal(256, music.BitrateKbps);

        var voice = AudioProfile.ForMode(StreamMode.Camera, 160, 256);
        Assert.False(voice.Music);
        Assert.Equal(480, voice.FrameSamples);
        Assert.Equal("lowdelay", voice.OpusApplication);
        Assert.True(voice.PlaybackTargetMs < music.PlaybackTargetMs);

        Assert.Equal(AudioProfile.MaxMusicKbps, AudioProfile.ForMusic(999).BitrateKbps);
    }

    [Theory]
    [InlineData("4K", 60, "Máximo")]
    [InlineData("1440p", 165, "Máximo")]
    [InlineData("1440p", 60, "Ultra")]
    [InlineData("1080p", 60, "Alto")]
    [InlineData("720p", 60, "Leve")]
    public void QualityTiers(string resolution, int fps, string expected)
        => Assert.Equal(expected, Quality.TierLabel(Resolution.FromLabel(resolution), fps));

    [Fact]
    public void Settings_NormalizeNewFields()
    {
        var settings = new AppSettings { Mode = (StreamMode)42, CameraFps = 7, CameraResolution = "8K", MusicBitrateKbps = 5000, CameraId = "  " };
        settings.Normalize();
        Assert.Equal(StreamMode.Screen, settings.Mode);
        Assert.Equal(30, settings.CameraFps);
        Assert.Equal("1080p", settings.CameraResolution);
        Assert.Equal(AudioProfile.MaxMusicKbps, settings.MusicBitrateKbps);
        Assert.Null(settings.CameraId);
        Assert.True(settings.GpuVideoRenderer);
    }

    [Fact]
    public void Settings_KeepModeAcrossSaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Current.Mode = StreamMode.AudioOnly;
            store.Current.MusicBitrateKbps = 192;
            store.Save();
            var reloaded = new SettingsStore(path).Current;
            Assert.Equal(StreamMode.AudioOnly, reloaded.Mode);
            Assert.Equal(192, reloaded.MusicBitrateKbps);
            Assert.Contains("\"AudioOnly\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Labels_AreFriendly()
    {
        Assert.Equal("Festa de música", StreamModes.Label(StreamMode.AudioOnly));
        Assert.False(StreamModes.HasVideo(StreamMode.AudioOnly));
        Assert.True(StreamModes.HasVideo(StreamMode.Camera));
        Assert.StartsWith("Câmera · ", CaptureTarget.FromCamera("id", "Logitech", 1920, 1080, 30).Label);
    }
}

public class AudioMeterTests
{
    private static float[] Sine(double hz, int samples, float amplitude = 1f)
    {
        var data = new float[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / AudioProfile.SampleRate));
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        return data;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(8000)]
    public void FindsTheToneBand(double hz)
    {
        var meter = new AudioMeter(48);
        meter.AddInterleaved(Sine(hz, 8192));
        var bands = new float[meter.BandCount];
        meter.CopyBands(bands);

        var loudest = Array.IndexOf(bands, bands.Max());
        var center = 40 * Math.Pow(16_000 / 40.0, (loudest + 0.5) / 48);
        Assert.InRange(hz / center, 0.6, 1.7);
        Assert.InRange(bands[loudest], 0.85f, 1.0f);
        Assert.InRange(meter.Level, 0.85f, 1.0f);
        Assert.True(meter.Updates > 0);
    }

    [Fact]
    public void SilenceStaysAtZero()
    {
        var meter = new AudioMeter(32);
        meter.AddInterleaved(new float[8192]);
        var bands = new float[32];
        meter.CopyBands(bands);
        Assert.All(bands, b => Assert.Equal(0f, b));
        Assert.Equal(0f, meter.Level);
    }

    [Fact]
    public void QuietToneIsLowerThanLoudTone()
    {
        var loud = new AudioMeter(32);
        var quiet = new AudioMeter(32);
        loud.AddInterleaved(Sine(440, 8192, 1f));
        quiet.AddInterleaved(Sine(440, 8192, 0.01f)); // −40 dB
        Assert.InRange(quiet.Level, 0.2f, 0.45f);
        Assert.True(loud.Level > quiet.Level + 0.4f);
    }
}
