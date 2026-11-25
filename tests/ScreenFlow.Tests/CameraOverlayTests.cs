using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Tela, câmera e áudio juntos: a câmera vai numa segunda faixa de vídeo que não se mistura com a tela.</summary>
public class CameraOverlayTests
{
    private static byte[] RandomFrame(int size) => RandomNumberGenerator.GetBytes(size);

    [Theory]
    [InlineData(MediaPacketType.CameraVideo)]
    [InlineData(MediaPacketType.CameraFec)]
    public void Header_AcceptsCameraTypes(MediaPacketType type)
    {
        var buffer = new byte[MediaHeader.Size];
        new MediaHeader { Type = type, FrameId = 3, Count = 2 }.Write(buffer);
        Assert.True(MediaHeader.TryRead(buffer, out var read));
        Assert.Equal(type, read.Type);
    }

    [Fact]
    public void Packetizer_UsesCameraParityForCameraFrames()
    {
        var chunks = Packetizer.Packetize(MediaPacketType.CameraVideo, 1, RandomFrame(25_000), 0, true, fecGroupSize: 10);
        Assert.Contains(chunks, c => c.Header.Type == MediaPacketType.CameraFec);
        Assert.DoesNotContain(chunks, c => c.Header.Type is MediaPacketType.Video or MediaPacketType.VideoFec);
        Assert.Equal(MediaPacketType.CameraVideo, VideoTrack.DataType(VideoTrack.Camera));
        Assert.Equal(MediaPacketType.Video, VideoTrack.DataType(VideoTrack.Main));
        Assert.Null(VideoTrack.FecType(MediaPacketType.Audio));
    }

    [Fact]
    public void Reassemblers_KeepScreenAndCameraApart()
    {
        var screenFrame = RandomFrame(12_000);
        var cameraFrame = RandomFrame(9_000);
        // Mesmo número de frame nas duas faixas: cada uma tem a própria contagem.
        var screen = Packetizer.Packetize(MediaPacketType.Video, 5, screenFrame, 100, true);
        var camera = Packetizer.Packetize(MediaPacketType.CameraVideo, 5, cameraFrame, 100, true);
        var screenReassembler = new FrameReassembler();
        var cameraReassembler = new FrameReassembler(dataType: MediaPacketType.CameraVideo);
        var screenReady = new List<AssembledFrame>();
        var cameraReady = new List<AssembledFrame>();
        // Pacotes das duas faixas intercalados, como chegam pela rede; um pacote da câmera se perde e o FEC recupera.
        var interleaved = Enumerable.Range(0, Math.Max(screen.Count, camera.Count))
            .SelectMany(i => new[] { i < screen.Count ? screen[i] : null, i < camera.Count ? camera[i] : null })
            .OfType<MediaChunk>()
            .Where(c => !(c.Header.Type == MediaPacketType.CameraVideo && c.Header.Index == 2));
        foreach (var chunk in interleaved)
        {
            screenReady.AddRange(screenReassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.Zero));
            cameraReady.AddRange(cameraReassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.Zero));
        }
        Assert.Equal(screenFrame, Assert.Single(screenReady).Data);
        Assert.Equal(cameraFrame, Assert.Single(cameraReady).Data);
        Assert.Equal(1, cameraReassembler.PacketsRecoveredByFec);
        Assert.Equal(0, screenReassembler.PacketsRecoveredByFec);
    }

    [Fact]
    public void Reassembler_RejectsNonVideoTrack()
        => Assert.Throws<ArgumentException>(() => new FrameReassembler(dataType: MediaPacketType.Audio));

    [Fact]
    public void RetransmitCache_AnswersOnlyItsTrack()
    {
        var camera = Packetizer.Packetize(MediaPacketType.CameraVideo, 9, RandomFrame(5_000), 0, false, fecGroupSize: 0);
        var cameraCache = new RetransmitCache(8, MediaPacketType.CameraVideo);
        var screenCache = new RetransmitCache(8);
        cameraCache.Store(9, camera);
        screenCache.Store(9, camera);
        Assert.Equal(2, cameraCache.Lookup(9, [1, 3]).Count);
        Assert.Empty(screenCache.Lookup(9, [1, 3]));
    }

    [Fact]
    public void TrackRequests_RoundTripAndDefaultToTheScreen()
    {
        var nack = Assert.IsType<NackMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(new NackMessage(4, [1, 2], VideoTrack.Camera)).AsSpan(4)));
        Assert.Equal(VideoTrack.Camera, nack.Track);
        var keyframe = Assert.IsType<KeyframeRequestMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(new KeyframeRequestMessage("perda", VideoTrack.Camera)).AsSpan(4)));
        Assert.Equal(VideoTrack.Camera, keyframe.Track);

        // Como a 1.0.x envia: sem "track". Vale como pedido da tela.
        var old = Assert.IsType<NackMessage>(ControlFraming.Deserialize("""{"t":"nack","frameId":4,"missing":[1]}"""u8));
        Assert.Equal(VideoTrack.Main, old.Track);
        var oldKeyframe = Assert.IsType<KeyframeRequestMessage>(ControlFraming.Deserialize("""{"t":"keyframe","reason":"entrada"}"""u8));
        Assert.Equal(VideoTrack.Main, oldKeyframe.Track);
    }

    [Fact]
    public void ConfigAndDiscovery_CarryTheCamera()
    {
        var config = new StreamConfigMessage(VideoCodec.Hevc, 2560, 1440, 165, 40_000, true, 48000, 2, "hevc_nvenc", "x", CameraOverlay: true);
        var parsed = Assert.IsType<StreamConfigMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(config).AsSpan(4)));
        Assert.True(parsed.CameraOverlay);

        const string oldConfig = """{"t":"config","codec":"Hevc","width":1920,"height":1080,"fps":60,"bitrateKbps":20000,"audioEnabled":true,"audioSampleRate":48000,"audioChannels":2,"encoderName":"hevc_nvenc","reason":"x"}""";
        Assert.False(Assert.IsType<StreamConfigMessage>(ControlFraming.Deserialize(Encoding.UTF8.GetBytes(oldConfig))).CameraOverlay);

        var announcement = new DiscoveryAnnouncement("ScreenFlow", 1, "PC", "26.1.2.3", 47801, "1.1.0", false, 2, StreamMode.Screen, CameraOverlay: true);
        Assert.True(DiscoveryAnnouncement.TryParse(announcement.ToBytes(), out var round));
        Assert.True(round!.CameraOverlay);
        Assert.True(DiscoveryAnnouncement.TryParse("""{"app":"ScreenFlow","protocol":1,"hostName":"PC","address":"26.1.2.3","controlPort":47801,"version":"1.0.1","pinRequired":false,"viewers":0}"""u8, out var old));
        Assert.False(old!.CameraOverlay);
    }

    [Fact]
    public void ViewerAnnouncesCameraOverlay()
    {
        Assert.Contains(StreamModes.FeatureCameraOverlay, StreamModes.ViewerFeatures);
        var hello = new HelloMessage(1, "1.1.0", "PC", [VideoCodec.H264], 1920, 1080, 60, 5000, "k", [.. StreamModes.ViewerFeatures]);
        Assert.True(hello.Supports(StreamModes.FeatureCameraOverlay));
        Assert.False(new HelloMessage(1, "1.0.1", "PC", [VideoCodec.H264], 1920, 1080, 60, 5000, "k", [StreamModes.FeatureAudioOnly, StreamModes.FeatureCamera])
            .Supports(StreamModes.FeatureCameraOverlay));
    }

    [Fact]
    public void Labels_DescribeEverythingThatIsLive()
    {
        Assert.Equal("tela, câmera e áudio", StreamModes.Summary(StreamMode.Screen, audio: true, cameraWithScreen: true));
        Assert.Equal("tela e câmera", StreamModes.Summary(StreamMode.Screen, audio: false, cameraWithScreen: true));
        Assert.Equal("Tela + câmera", StreamModes.Label(StreamMode.Screen, cameraWithScreen: true));
        Assert.Equal("Tela", StreamModes.Label(StreamMode.Screen));
        Assert.Equal("Câmera", StreamModes.Label(StreamMode.Camera, cameraWithScreen: true));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720, 1280, 720)]
    [InlineData(640, 480, 1280, 720, 640, 480)]
    [InlineData(1280, 1024, 1280, 720, 900, 720)]
    [InlineData(1279, 719, 1280, 720, 1278, 718)]
    [InlineData(0, 0, 1280, 720, 1280, 720)]
    public void FitWithin_ShrinksWithoutDistorting(int width, int height, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
        => Assert.Equal((expectedWidth, expectedHeight), Quality.FitWithin(width, height, maxWidth, maxHeight));

    [Fact]
    public void CameraBitrate_IsSmallNextToTheScreen()
    {
        Assert.Equal(2100, Quality.CameraOverlayBitrateKbps(1280, 720, 30, VideoCodec.Hevc));
        Assert.Equal(2900, Quality.CameraOverlayBitrateKbps(1280, 720, 30, VideoCodec.H264));
        Assert.Equal(600, Quality.CameraOverlayBitrateKbps(320, 180, 15, VideoCodec.Hevc));
        Assert.True(Quality.CameraOverlayBitrateKbps(1280, 720, 30, VideoCodec.Hevc) < Quality.TargetBitrateKbps(Resolution.P1080, 60, VideoCodec.Hevc) / 4);
    }

    [Fact]
    public void Settings_KeepCameraCornerInRange()
    {
        var settings = new AppSettings { PlayerCameraCorner = 7, CameraWithScreen = true };
        settings.Normalize();
        Assert.Equal(0, settings.PlayerCameraCorner);
        Assert.True(settings.CameraWithScreen);
    }
}
