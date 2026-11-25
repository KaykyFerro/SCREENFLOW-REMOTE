using System.Runtime.InteropServices;
using ScreenFlow.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenFlow.Infrastructure.Rendering;

/// <summary>
/// Desenha o vídeo decodificado direto na GPU: a textura NV12 que o decoder D3D11VA produz vai pelo Video Processor
/// (conversão para RGB e escala na GPU) para a swapchain da janela do player. Nenhum frame passa pela CPU.
/// Usa flip model com tearing quando o sistema permite, para a menor latência possível.
/// O decoder usa o mesmo device (<see cref="DevicePointer"/>), então as texturas não são copiadas entre devices.
/// </summary>
public sealed class D3D11VideoPresenter : IDisposable
{
    private const int BufferCount = 2;
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly SwapChainFlags _swapFlags;
    private readonly bool _tearing;
    private readonly Dictionary<(nint Texture, int Slice), ID3D11VideoProcessorInputView> _inputViews = [];
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _inputWidth, _inputHeight;
    private int _outputWidth, _outputHeight;
    private int _pendingWidth, _pendingHeight;
    private bool _resizePending;
    private bool _disposed;
    private long _framesPresented;

    public D3D11VideoPresenter(nint hwnd, int width, int height)
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
            throw new InvalidOperationException($"Sem Direct3D 11 por hardware para o player ({result.Code:X8}).");
        _device = device;
        _context = context;
        try
        {
            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
                multithread?.SetMultithreadProtected(true);
            _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = context.QueryInterface<ID3D11VideoContext>();

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                AdapterName = adapter.Description.Description.TrimEnd('\0');
                using var factory = adapter.GetParent<IDXGIFactory2>();
                using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                    _tearing = factory5?.PresentAllowTearing ?? false;
                _swapFlags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None;
                _outputWidth = Math.Max(1, width);
                _outputHeight = Math.Max(1, height);
                _swapChain = factory.CreateSwapChainForHwnd(device, hwnd, new SwapChainDescription1
                {
                    Width = (uint)_outputWidth,
                    Height = (uint)_outputHeight,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = BufferCount,
                    BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0),
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = _swapFlags,
                });
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            }
            CreateOutput();
            Clear();
            Log.Info($"Player na GPU: {AdapterName}, {_outputWidth}x{_outputHeight}, tearing={_tearing}");
        }
        catch
        {
            _context.Dispose();
            _device.Dispose();
            throw;
        }
    }

    public string AdapterName { get; } = "";

    /// <summary>ID3D11Device* para o decoder D3D11VA usar o mesmo device.</summary>
    public nint DevicePointer => _device.NativePointer;

    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>Falhou (driver reiniciou, formato sem suporte): o player volta para o caminho pela CPU.</summary>
    public bool Failed { get; private set; }

    /// <summary>Novo tamanho da área de vídeo em pixels. Aplicado no próximo frame (na thread do decoder).</summary>
    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            _pendingWidth = Math.Max(1, width);
            _pendingHeight = Math.Max(1, height);
            _resizePending = _pendingWidth != _outputWidth || _pendingHeight != _outputHeight;
        }
    }

    /// <summary>Esquece as views das texturas do decoder (chamado quando o decoder é recriado).</summary>
    public void ResetInputs()
    {
        lock (_gate)
        {
            foreach (var view in _inputViews.Values) view.Dispose();
            _inputViews.Clear();
        }
    }

    /// <summary>Mostra uma fatia do array de texturas NV12 do decoder. Retorna false se não deu (o chamador usa a CPU).</summary>
    public bool Present(nint texture, int slice, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed || Failed) return false;
            try
            {
                ApplyResize();
                EnsureProcessor(width, height);
                var input = GetInputView(texture, slice);
                _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, width, height));
                _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, Fit(width, height, _outputWidth, _outputHeight));
                _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, _outputWidth, _outputHeight));
                _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = input }]);
                var presented = _swapChain.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
                if (presented.Failure) throw new SharpGenException(presented);
                Interlocked.Increment(ref _framesPresented);
                return true;
            }
            catch (Exception exception) when (exception is SharpGenException or InvalidOperationException)
            {
                Log.Warn($"Player na GPU falhou ({exception.Message}). Voltando para a CPU.");
                Failed = true;
                return false;
            }
        }
    }

    /// <summary>Pinta de preto (troca de modo, fim da transmissão).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed || Failed) return;
            try
            {
                ApplyResize();
                using var target = _device.CreateRenderTargetView(_backBuffer!);
                _context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
                _swapChain.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
            }
            catch (SharpGenException exception) { Log.Debug($"Clear do player falhou: {exception.Message}"); }
        }
    }

    /// <summary>Área do vídeo dentro da janela, mantendo a proporção (tarjas pretas).</summary>
    public static RawRect Fit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return new RawRect(0, 0, targetWidth, targetHeight);
        var scale = Math.Min(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight);
        var w = (int)Math.Round(sourceWidth * scale);
        var h = (int)Math.Round(sourceHeight * scale);
        var x = (targetWidth - w) / 2;
        var y = (targetHeight - h) / 2;
        return new RawRect(x, y, x + w, y + h);
    }

    private void ApplyResize()
    {
        if (!_resizePending) return;
        _resizePending = false;
        ReleaseOutput();
        _swapChain.ResizeBuffers(BufferCount, (uint)_pendingWidth, (uint)_pendingHeight, Format.B8G8R8A8_UNorm, _swapFlags).CheckError();
        _outputWidth = _pendingWidth;
        _outputHeight = _pendingHeight;
        CreateOutput();
    }

    private void CreateOutput()
    {
        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        // O processador depende do tamanho de saída: recriado no próximo frame.
        ReleaseProcessor();
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_processor is not null && _outputView is not null && width == _inputWidth && height == _inputHeight) return;
        ReleaseProcessor();
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)_outputWidth,
            OutputHeight = (uint)_outputHeight,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        });
        _enumerator.CheckVideoProcessorFormat(Format.NV12, out var inputSupport).CheckError();
        _enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out var outputSupport).CheckError();
        if ((inputSupport & VideoProcessorFormatSupport.Input) == 0 || (outputSupport & VideoProcessorFormatSupport.Output) == 0)
            throw new InvalidOperationException("O Video Processor da GPU não converte NV12 para BGRA.");
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_backBuffer!, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        // YUV limitado BT.709 (o que o host gera) → RGB completo da tela.
        using (var context1 = _videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>())
        {
            if (context1 is not null)
            {
                context1.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.YcbcrStudioG22LeftP709);
                context1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
            }
        }
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _inputWidth = width;
        _inputHeight = height;
        // As views de entrada pertencem ao enumerador antigo.
        foreach (var view in _inputViews.Values) view.Dispose();
        _inputViews.Clear();
    }

    private ID3D11VideoProcessorInputView GetInputView(nint texture, int slice)
    {
        if (_inputViews.TryGetValue((texture, slice), out var cached)) return cached;
        Marshal.AddRef(texture); // o wrapper libera ao sair do using
        using var resource = new ID3D11Texture2D(texture);
        var view = _videoDevice.CreateVideoProcessorInputView(resource, _enumerator!, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
        });
        if (_inputViews.Count > 64)
        {
            foreach (var old in _inputViews.Values) old.Dispose();
            _inputViews.Clear();
        }
        _inputViews[(texture, slice)] = view;
        return view;
    }

    private void ReleaseProcessor()
    {
        foreach (var view in _inputViews.Values) view.Dispose();
        _inputViews.Clear();
        _outputView?.Dispose();
        _outputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _inputWidth = _inputHeight = 0;
    }

    private void ReleaseOutput()
    {
        ReleaseProcessor();
        _backBuffer?.Dispose();
        _backBuffer = null;
        _context.ClearState();
        _context.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseOutput();
            _swapChain.Dispose();
            _videoContext.Dispose();
            _videoDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
