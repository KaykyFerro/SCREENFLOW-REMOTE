namespace ScreenFlow.Core;

public enum CaptureKind { Monitor, Window, Camera }

/// <summary>Monitor, janela ou câmera escolhido no seletor da tela Transmitir.</summary>
public sealed record CaptureTarget(CaptureKind Kind, string Id, string Label, int Width, int Height, int RefreshRateHz, nint Handle = 0)
{
    public static CaptureTarget FromMonitor(DisplayMonitor monitor)
        => new(CaptureKind.Monitor, monitor.DeviceName, "Monitor · " + monitor.DisplayLabel, monitor.Width, monitor.Height, monitor.RefreshRateHz);

    /// <summary>Câmera: o Id é o identificador do dispositivo no Windows; o tamanho é o desejado (a câmera escolhe o mais próximo).</summary>
    public static CaptureTarget FromCamera(string deviceId, string name, int width, int height, int fps)
        => new(CaptureKind.Camera, deviceId, "Câmera · " + name, width, height, fps);

    public override string ToString() => Label;
}
