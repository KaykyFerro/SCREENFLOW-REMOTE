namespace ScreenFlow.Core;

public sealed record DisplayMonitor(string DeviceName, string FriendlyName, int Width, int Height, int RefreshRateHz, bool IsPrimary, int X, int Y)
{
    public string DisplayLabel => $"{FriendlyName} — {Width} × {Height} @ {RefreshRateHz} Hz" + (IsPrimary ? " (Principal)" : string.Empty);
}