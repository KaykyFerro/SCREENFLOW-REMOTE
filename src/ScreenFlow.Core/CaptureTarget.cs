namespace ScreenFlow.Core;

public enum CaptureKind { Monitor, Window }

/// <summary>Monitor ou janela escolhido no seletor da tela Transmitir.</summary>
public sealed record CaptureTarget(CaptureKind Kind, string Id, string Label, int Width, int Height, int RefreshRateHz, nint Handle = 0)
{
    public static CaptureTarget FromMonitor(DisplayMonitor monitor)
        => new(CaptureKind.Monitor, monitor.DeviceName, "Monitor · " + monitor.DisplayLabel, monitor.Width, monitor.Height, monitor.RefreshRateHz);

    public override string ToString() => Label;
}
