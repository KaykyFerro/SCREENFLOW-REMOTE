using ScreenFlow.Core;
using Xunit;

namespace ScreenFlow.Tests;

public class DisplayMonitorTests
{
    [Fact]
    public void DisplayLabel_ContainsResolutionAndRefresh()
    {
        var monitor = new DisplayMonitor("DISPLAY1", "Display", 1920, 1080, 144, true, 0, 0);
        Assert.Contains("1920 × 1080", monitor.DisplayLabel);
        Assert.Contains("144 Hz", monitor.DisplayLabel);
        Assert.Contains("Principal", monitor.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_HandlesUnavailableRefresh()
    {
        var monitor = new DisplayMonitor("DISPLAY1", "Display", 1280, 720, 0, false, 0, 0);
        Assert.Contains("1280 × 720", monitor.DisplayLabel);
    }
}
