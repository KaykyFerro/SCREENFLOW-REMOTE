using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenFlow.Infrastructure.Capture;

/// <summary>
/// Converte BGRA (captura) para NV12 (encoder) e redimensiona na GPU usando o Video Processor do D3D11.
/// O frame nunca passa pela CPU.
/// </summary>
public sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11VideoProcessorOutputView _outputView;

    public GpuColorConverter(GpuDevice gpu, ID3D11Texture2D source, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, int fps)
    {
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)sourceWidth,
            InputHeight = (uint)sourceHeight,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        Output = gpu.CreateTexture(outputWidth, outputHeight, Format.NV12, BindFlags.RenderTarget | BindFlags.ShaderResource);

        _inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        _outputView = _videoDevice.CreateVideoProcessorOutputView(Output, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        // RGB completo (desktop) → YUV limitado BT.709 (o que os decoders esperam).
        using var context1 = _videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>();
        if (context1 is not null)
        {
            context1.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            context1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    public ID3D11Texture2D Output { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    public void Convert()
    {
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, [stream]);
    }

    public void Dispose()
    {
        _outputView.Dispose();
        _inputView.Dispose();
        Output.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
