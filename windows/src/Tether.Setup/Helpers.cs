using System.Security;

namespace Tether.Setup;

public sealed record SetupArgs(string Command, string Server, string Email, string Child, string? Error)
{
    public static SetupArgs Parse(string[] args)
    {
        string Fail(string why) => why;
        var cmd = args.Length > 0 ? args[0] : "";
        if (cmd == "uninstall") return new(cmd, "", "", "", null);
        if (cmd != "install") return new(cmd, "", "", "", "usage: tether-setup install --server <url> --email <parent email> --child <windows username> | uninstall");

        string server = "", email = "", child = "";
        for (var i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--server": server = Next() ?? ""; break;
                case "--email": email = Next() ?? ""; break;
                case "--child": child = Next() ?? ""; break;
                case "--password": return new(cmd, server, email, child, Fail("the password is prompted for; it is never taken from the command line"));
                default: return new(cmd, server, email, child, Fail($"unknown argument {args[i]}"));
            }
        }
        if (!Uri.TryCreate(server, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return new(cmd, server, email, child, Fail("--server must be an http(s) URL"));
        if (email.Length == 0) return new(cmd, server, email, child, Fail("--email is required"));
        if (child.Length == 0) return new(cmd, server, email, child, Fail("--child is required"));
        return new(cmd, server, email, child, null);
    }
}

public static class TaskXml
{
    /// <summary>Scheduled task that starts the helper when the child, and only the child, signs in. Runs as that user in their session, with no stored password.</summary>
    public static string Build(string childAccount, string helperExe)
    {
        var user = SecurityElement.Escape(childAccount);
        var cmd = SecurityElement.Escape(helperExe);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Tether helper for the child account</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
    <AllowStartOnDemand>true</AllowStartOnDemand>
  </Settings>
  <Actions Context="Author"><Exec><Command>{cmd}</Command></Exec></Actions>
</Task>
""";
    }
}

public static class ServiceCommands
{
    public const string Name = "TetherService";

    /// <summary>Arguments for `sc.exe`: restart after any crash, three times, then keep doing so (the counter resets daily).</summary>
    public static string[] Failure(string name)
        => new[] { "failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000" };
}
