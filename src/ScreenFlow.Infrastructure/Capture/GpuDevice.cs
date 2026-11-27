using ScreenFlow.Core.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Device D3D11 compartilhado por captura, conversão de cor e encoder. Com proteção multithread ligada,
/// o mesmo contexto pode ser usado pelo callback de captura e pela thread do encoder.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private GpuDevice(IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        using var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>();
        multithread?.SetMultithreadProtected(true);
        AdapterName = adapter.Description1.Description.TrimEnd('\0');
        VendorId = adapter.Description1.VendorId;
    }

    public IDXGIAdapter1 Adapter { get; }
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }
    public uint VendorId { get; }

    /// <summary>Cria o device no adaptador que controla o monitor (evita cópia entre GPUs em notebooks híbridos).</summary>
    public static GpuDevice Create(string? monitorDeviceName = null)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var owns = false;
            if (monitorDeviceName is not null)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                        if (string.Equals(output.Description.DeviceName.TrimEnd('\0'), monitorDeviceName, StringComparison.OrdinalIgnoreCase)) owns = true;
                }
            }
            if (chosen is null && (adapter.Description1.Flags & AdapterFlags.Software) == 0 || owns)
            {
                chosen?.Dispose();
                chosen = adapter;
                if (owns) break;
            }
            else adapter.Dispose();
        }
        if (chosen is null) throw new InvalidOperationException("Nenhuma GPU com suporte a Direct3D 11 foi encontrada.");

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
            throw new InvalidOperationException($"Falha ao criar o device D3D11 ({result.Code:X8}).");
        var gpu = new GpuDevice(chosen, device, context);
        Log.Info($"GPU de captura: {gpu.AdapterName}");
        return gpu;
    }

    /// <summary>O device de software do Windows (WARP), para o autoteste em PCs sem placa de vídeo, como o CI.</summary>
    public static GpuDevice CreateSoftware()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        var adapter = factory.EnumWarpAdapter<IDXGIAdapter1>();
        var result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
        {
            adapter.Dispose();
            throw new InvalidOperationException($"Falha ao criar o device WARP ({result.Code:X8}).");
        }
        return new GpuDevice(adapter, device, context);
    }

    public ID3D11Texture2D CreateTexture(int width, int height, Format format, BindFlags bind, ResourceUsage usage = ResourceUsage.Default, CpuAccessFlags cpu = CpuAccessFlags.None)
        => Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpu,
        });

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
