namespace ScreenFlow.Core;

public sealed record DisplayMonitor(string DeviceName, string FriendlyName, int Width, int Height, int RefreshRateHz, bool IsPrimary, int X, int Y, nint Handle = 0)
{
    public string DisplayLabel => $"{FriendlyName} — {Width} × {Height}" + (RefreshRateHz > 0 ? $" @ {RefreshRateHz} Hz" : string.Empty) + (IsPrimary ? " (Principal)" : string.Empty);
}
