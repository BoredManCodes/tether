using System.Drawing;
using System.Windows.Forms;
using Tether.Helper;

namespace Tether.Tests;

public class ScreenCaptureTests
{
    [Fact]
    public void CaptureJpeg_returns_a_jpeg_scaled_to_1280_wide_at_most()
    {
        var primary = Screen.PrimaryScreen!.Bounds;
        var jpeg = ScreenCapture.CaptureJpeg();
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        using var img = Image.FromStream(new MemoryStream(jpeg));
        Assert.Equal(Math.Min(1280, ScreenCapture.PrimaryPixelSize().Width), img.Width);
        Assert.True(img.Height > 0);
    }

    [Fact]
    public void CaptureJpeg_never_upscales_a_narrow_screen()
        => Assert.True(ScreenCapture.TargetWidth(1024, 1280) == 1024);

    [Fact]
    public void TargetWidth_scales_down_wide_screens()
        => Assert.True(ScreenCapture.TargetWidth(3840, 1280) == 1280);
}
