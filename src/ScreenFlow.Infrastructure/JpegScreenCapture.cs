using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ScreenFlow.Core;

namespace ScreenFlow.Infrastructure;

/// <summary>
/// GDI-based compatibility capture used by the first LAN MVP.
/// This is deliberately capped below the product's 4K/165 target; it is not the future GPU/WGC pipeline.
/// </summary>
public static class JpegScreenCapture
{
    public static byte[] Capture(DisplayMonitor monitor, int maximumWidth, long jpegQuality = 76)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (maximumWidth is < 320 or > 3840) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (jpegQuality is < 30 or > 95) throw new ArgumentOutOfRangeException(nameof(jpegQuality));

        var screen = Screen.AllScreens.FirstOrDefault(x => string.Equals(x.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"O monitor {monitor.DeviceName} não está mais disponível.");

        var bounds = screen.Bounds;
        using var captured = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(captured))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, captured.Size, CopyPixelOperation.SourceCopy);
        }

        Bitmap? resized = null;
        try
        {
            Image frame = captured;
            if (captured.Width > maximumWidth)
            {
                var targetHeight = Math.Max(1, (int)Math.Round(captured.Height * (maximumWidth / (double)captured.Width)));
                resized = new Bitmap(maximumWidth, targetHeight, PixelFormat.Format24bppRgb);
                using var graphics = Graphics.FromImage(resized);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                graphics.DrawImage(captured, new Rectangle(0, 0, maximumWidth, targetHeight));
                frame = resized;
            }

            using var output = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            frame.Save(output, jpeg, parameters);
            return output.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }
}