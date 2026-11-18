using Tether.Shared;

namespace Tether.Tests;

public class ExePathsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\",0", "firefox.exe")]
    [InlineData("C:\\Program Files\\App\\App.EXE,-101", "app.exe")]
    [InlineData("C:\\Program Files\\App\\tool.exe", "tool.exe")]
    public void FromDisplayIcon_returns_the_lowercase_exe_name(string value, string expected)
        => Assert.Equal(expected, ExePaths.FromDisplayIcon(value));

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Windows\\icons\\thing.ico")]
    [InlineData("C:\\Program Files\\App\\unins000.exe")]
    [InlineData("C:\\Program Files\\App\\uninstall.exe,0")]
    [InlineData("C:\\Windows\\Installer\\{GUID}\\icon.exe")]
    public void FromDisplayIcon_ignores_non_programs_and_uninstallers(string value)
        => Assert.Null(ExePaths.FromDisplayIcon(value));
}
