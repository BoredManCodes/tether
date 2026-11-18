using System.Xml.Linq;
using Tether.Setup;

namespace Tether.Tests;

public class SetupTests
{
    [Fact]
    public void Parse_install_reads_server_email_and_child()
    {
        var a = SetupArgs.Parse(new[] { "install", "--server", "https://t.example", "--email", "p@x.test", "--child", "Kid" });
        Assert.Equal("install", a.Command);
        Assert.Equal("https://t.example", a.Server);
        Assert.Equal("p@x.test", a.Email);
        Assert.Equal("Kid", a.Child);
        Assert.Null(a.Error);
    }

    [Theory]
    [InlineData("install", "--email", "a@b.test", "--child", "Kid")]                               // no server
    [InlineData("install", "--server", "https://t.example", "--child", "Kid")]                     // no email
    [InlineData("install", "--server", "https://t.example", "--email", "a@b.test")]                // no child
    [InlineData("install", "--server", "ftp://t.example", "--email", "a@b.test", "--child", "K")]  // not http(s)
    [InlineData("install", "--server", "https://t.example", "--email", "a@b.test", "--child")]     // dangling flag
    [InlineData("frobnicate")]
    public void Parse_reports_an_error_for_missing_or_bad_input(params string[] args)
        => Assert.NotNull(SetupArgs.Parse(args).Error);

    [Fact]
    public void Parse_refuses_a_password_on_the_command_line()
        => Assert.NotNull(SetupArgs.Parse(new[] { "install", "--server", "https://t.example", "--email", "a@b.test", "--child", "K", "--password", "x" }).Error);

    [Fact]
    public void Parse_uninstall_needs_no_other_arguments()
    {
        var a = SetupArgs.Parse(new[] { "uninstall" });
        Assert.Equal("uninstall", a.Command);
        Assert.Null(a.Error);
    }

    [Fact]
    public void Logon_task_xml_is_for_the_child_only_and_escapes_the_name()
    {
        var xml = XDocument.Parse(TaskXml.Build(@"PC\Kid & <Co>", @"C:\Program Files\Tether\Tether.Helper.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"PC\Kid & <Co>", xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal(@"PC\Kid & <Co>", xml.Descendants(ns + "Principal").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "Principal").Single().Element(ns + "LogonType")!.Value);
        Assert.Equal(@"C:\Program Files\Tether\Tether.Helper.exe", xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);   // never time out
    }

    [Fact]
    public void Service_recovery_restarts_on_every_failure()
    {
        var args = ServiceCommands.Failure("TetherService");
        Assert.Equal(new[] { "failure", "TetherService", "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000" }, args);
    }
}
