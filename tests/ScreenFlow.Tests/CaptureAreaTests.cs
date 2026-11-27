using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

public class CaptureAreaTests
{
    [Fact]
    public void NoRegionIsTheWholeSource()
        => Assert.Equal(new PixelRect(0, 0, 2560, 1440), CaptureArea.Default.RegionIn(2560, 1441));

    [Fact]
    public void RegionIsClampedAndEven()
    {
        var area = new CaptureArea(Region: new RelativeRect(0.5, 0.25, 0.5, 0.5));
        Assert.Equal(new PixelRect(1280, 360, 1280, 720), area.RegionIn(2560, 1440));
        var odd = new CaptureArea(Region: new RelativeRect(0.1, 0.1, 0.333, 0.333)).RegionIn(1921, 1081);
        Assert.Equal(0, odd.Width % 2);
        Assert.Equal(0, odd.Height % 2);
        // Fora da tela: puxada para dentro.
        var outside = new CaptureArea(Region: new RelativeRect(0.9, 0.9, 0.5, 0.5)).RegionIn(1920, 1080);
        Assert.True(outside.Right <= 1920 && outside.Bottom <= 1080 && outside.Width >= CaptureArea.MinRegionPixels);
        // Pequena demais: cresce até o mínimo.
        var tiny = new CaptureArea(Region: new RelativeRect(0.5, 0.5, 0.02, 0.02)).RegionIn(1920, 1080);
        Assert.Equal(CaptureArea.MinRegionPixels, tiny.Width);
    }

    [Fact]
    public void BrokenRegionFallsBackToWholeSource()
    {
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), new CaptureArea(Region: new RelativeRect(double.NaN, 0, 1, 1)).RegionIn(1920, 1080));
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), new CaptureArea(Region: new RelativeRect(0, 0, 1, 1)).RegionIn(1920, 1080));
    }

    [Fact]
    public void MasksFollowTheRegionAndStayInside()
    {
        var area = new CaptureArea(Region: new RelativeRect(0.5, 0, 0.5, 1), Masks: [RelativeRect.Notifications, new RelativeRect(0, 0, 0.1, 0.1)]);
        var masks = area.MasksIn(2000, 1000);
        // Só a das notificações cai dentro da metade direita; a do canto de cima à esquerda fica fora.
        var mask = Assert.Single(masks);
        Assert.Equal(new PixelRect(480, 620, 520, 380), mask);
    }

    [Fact]
    public void DescribesWhatIsHidden()
    {
        Assert.Equal("Tela inteira, com o cursor.", CaptureArea.Default.Describe());
        Assert.Contains("sem o cursor", new CaptureArea(ShowCursor: false).Describe());
        Assert.Contains("1 área tapada", new CaptureArea(Masks: [RelativeRect.Notifications]).Describe());
    }

    [Fact]
    public void SettingsBuildTheAreaAndSurviveARoundTrip()
    {
        var settings = new ScreenFlow.Core.Settings.AppSettings
        {
            CaptureShowCursor = false,
            CaptureRegion = new RelativeRect(-0.25, 0.125, 0.75, 0.5),
            MaskNotifications = true,
            CaptureMasks = [new RelativeRect(0.1, 0.1, 0.2, 0.2), new RelativeRect(double.NaN, 0, 1, 1)],
        };
        settings.Normalize();
        Assert.Equal(new RelativeRect(0, 0.125, 0.5, 0.5), settings.CaptureRegion);
        Assert.Single(settings.CaptureMasks);

        var area = settings.CaptureAreaNow();
        Assert.False(area.ShowCursor);
        Assert.Equal(2, area.Masks!.Count);
        Assert.Equal(RelativeRect.Notifications, area.Masks[0]);

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<ScreenFlow.Core.Settings.AppSettings>(json)!;
        Assert.Equal(settings.CaptureRegion, back.CaptureRegion);
        Assert.Equal(settings.CaptureMasks, back.CaptureMasks);
    }

    [Fact]
    public void OldSettingsWithoutTheFieldsShowEverything()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<ScreenFlow.Core.Settings.AppSettings>("{\"DeviceName\":\"PC\"}")!;
        old.Normalize();
        var area = old.CaptureAreaNow();
        Assert.True(area.ShowCursor);
        Assert.Null(area.Region);
        Assert.Empty(area.MasksIn(1920, 1080));
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), area.RegionIn(1920, 1080));
    }
}
