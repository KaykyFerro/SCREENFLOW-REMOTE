using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Desktop Duplication API (DXGI). Copia o frame do desktop para uma textura própria na GPU (sem passar pela CPU).
/// Usada como reserva quando o Windows.Graphics.Capture não está disponível. Não inclui o cursor.
/// </summary>
public sealed class DxgiDuplicationCapture : ICaptureSource
{
    private readonly GpuDevice _gpu;
    private readonly string _deviceName;
    private IDXGIOutputDuplication? _duplication;

    public DxgiDuplicationCapture(GpuDevice gpu, string monitorDeviceName)
    {
        _gpu = gpu;
        _deviceName = monitorDeviceName;
        CreateDuplication();
        Texture = gpu.CreateTexture(Width, Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
    }

    public string Backend => "DXGI Desktop Duplication";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D Texture { get; }
    public bool SizeChanged { get; private set; }

    private void CreateDuplication()
    {
        _duplication?.Dispose();
        for (uint o = 0; _gpu.Adapter.EnumOutputs(o, out var output).Success; o++)
        {
            using (output)
            {
                if (!string.Equals(output.Description.DeviceName.TrimEnd('\0'), _deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_gpu.Device);
                var mode = _duplication.Description.ModeDescription;
                var newWidth = (int)mode.Width;
                var newHeight = (int)mode.Height;
                if (Width != 0 && (newWidth != Width || newHeight != Height)) SizeChanged = true;
                Width = newWidth;
                Height = newHeight;
                return;
            }
        }
        throw new InvalidOperationException($"O monitor {_deviceName} não pertence à GPU {_gpu.AdapterName}. Conecte-o na GPU principal ou use captura de janela.");
    }

    public bool WaitForFrame(TimeSpan timeout, out long timestampUs)
    {
        timestampUs = MediaClock.NowUs;
        if (_duplication is null) return false;
        Result result;
        IDXGIResource? resource = null;
        try
        {
            result = _duplication.AcquireNextFrame((uint)Math.Max(0, timeout.TotalMilliseconds), out var info, out resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout) return false;
            if (result == Vortice.DXGI.ResultCode.AccessLost)
            {
                Log.Warn("Desktop Duplication perdeu o acesso (troca de modo/UAC). Recriando.");
                Thread.Sleep(50);
                CreateDuplication();
                return false;
            }
            result.CheckError();
            if (info.LastPresentTime == 0 || resource is null)
            {
                _duplication.ReleaseFrame();
                return false; // só o cursor mudou
            }
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _gpu.Context.CopyResource(Texture, texture);
            _duplication.ReleaseFrame();
            return true;
        }
        catch (SharpGenException exception)
        {
            Log.Warn($"Falha na captura DXGI: {exception.Message}");
            try { CreateDuplication(); } catch { Thread.Sleep(200); }
            return false;
        }
        finally
        {
            resource?.Dispose();
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        Texture.Dispose();
    }
}
