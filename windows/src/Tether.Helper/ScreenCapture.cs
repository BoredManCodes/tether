using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Tether.Helper;

/// <summary>Primary-monitor screenshot as a scaled JPEG. GDI copy works for a standard user in an interactive session.</summary>
public static class ScreenCapture
{
    public static Size PrimaryPixelSize() => Screen.PrimaryScreen!.Bounds.Size;

    /// <summary>Never upscales: a 1024-wide screen stays 1024.</summary>
    public static int TargetWidth(int sourceWidth, int maxWidth) => Math.Min(sourceWidth, maxWidth);

    public static byte[] CaptureJpeg(int maxWidth = 1280, long quality = 60)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

        var w = TargetWidth(bounds.Width, maxWidth);
        var h = Math.Max(1, (int)Math.Round(bounds.Height * (w / (double)bounds.Width)));
        using var scaled = w == bounds.Width ? null : new Bitmap(w, h, PixelFormat.Format24bppRgb);
        if (scaled != null)
        {
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, w, h);
        }

        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        (scaled ?? full).Save(ms, jpeg, ep);
        return ms.ToArray();
    }
}
