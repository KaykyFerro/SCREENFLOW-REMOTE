using System.Runtime.InteropServices;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Windows.Graphics.Capture: captura monitor ou janela com cursor, direto em textura D3D11.
/// Os frames chegam num pool free-threaded e são copiados na GPU para a textura de trabalho.
/// </summary>
public sealed class WgcCapture : ICaptureSource
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly GpuDevice _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _frameReady = new(false);
    private long _lastTimestampUs;
    private bool _disposed;

    public WgcCapture(GpuDevice gpu, nint monitorHandle, nint windowHandle, int fps)
    {
        _gpu = gpu;
        _item = windowHandle != 0 ? CreateItemForWindow(windowHandle) : CreateItemForMonitor(monitorHandle);
        _item.Closed += (_, _) => { SizeChanged = true; _frameReady.Set(); };

        _winrtDevice = CreateWinRtDevice(gpu.Device);
        var size = _item.Size;
        Width = Math.Max(2, size.Width & ~1);
        Height = Math.Max(2, size.Height & ~1);
        Texture = gpu.CreateTexture(Width, Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        TrySet(() => _session.IsCursorCaptureEnabled = true);
        TrySet(() => _session.IsBorderRequired = false);              // Windows 11
        TrySet(() => _session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / Math.Max(30, fps))); // Windows 11 24H2
        _session.StartCapture();
        Log.Info($"WGC iniciado: {_item.DisplayName} {Width}x{Height}");
    }

    public string Backend => "Windows.Graphics.Capture";
    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Texture { get; }
    public bool SizeChanged { get; private set; }

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        if (frame.ContentSize.Width != Width && (frame.ContentSize.Width & ~1) != Width || (frame.ContentSize.Height & ~1) != Height)
        {
            SizeChanged = true;
            _frameReady.Set();
            return;
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = Texture2DIid;
        var pointer = access.GetInterface(ref iid);
        using (var texture = new ID3D11Texture2D(pointer))
        {
            _gpu.Context.CopySubresourceRegion(Texture, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
        }
        Interlocked.Exchange(ref _lastTimestampUs, MediaClock.NowUs);
        _frameReady.Set();
    }

    public bool WaitForFrame(TimeSpan timeout, out long timestampUs)
    {
        var got = _frameReady.WaitOne(timeout);
        timestampUs = Interlocked.Read(ref _lastTimestampUs);
        if (timestampUs == 0) timestampUs = MediaClock.NowUs;
        return got && !SizeChanged;
    }

    private static void TrySet(Action action)
    {
        try { action(); } catch { /* propriedade não existe nesta versão do Windows */ }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = interop.CreateForMonitor(hmonitor, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrameArrived;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        _winrtDevice.Dispose();
        Texture.Dispose();
        _frameReady.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }
}

public static class CaptureFactory
{
    /// <summary>WGC primeiro (inclui cursor e janelas); DXGI Duplication como reserva para monitores.</summary>
    public static ICaptureSource Create(GpuDevice gpu, ScreenFlow.Core.CaptureTarget target, ScreenFlow.Core.DisplayMonitor? monitor, int fps)
    {
        if (WgcCapture.IsSupported)
        {
            try
            {
                return target.Kind == ScreenFlow.Core.CaptureKind.Window
                    ? new WgcCapture(gpu, 0, target.Handle, fps)
                    : new WgcCapture(gpu, monitor?.Handle ?? 0, 0, fps);
            }
            catch (Exception exception)
            {
                Log.Warn($"Windows.Graphics.Capture falhou, tentando DXGI: {exception.Message}");
                if (target.Kind == ScreenFlow.Core.CaptureKind.Window) throw;
            }
        }
        if (monitor is null) throw new InvalidOperationException("Captura de janela exige Windows 10 1903 ou mais novo.");
        return new DxgiDuplicationCapture(gpu, monitor.DeviceName);
    }
}
