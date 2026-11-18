using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Tether.Service;
using Tether.Shared;

namespace Tether.Setup;

public static class Installer
{
    private const string TaskName = "Tether Helper";
    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tether");
    private static string ServiceExe => Path.Combine(InstallDir, "Tether.Service.exe");
    private static string HelperExe => Path.Combine(InstallDir, "Tether.Helper.exe");
    private static string SetupExe => Path.Combine(InstallDir, "Tether.Setup.exe");

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<int> InstallAsync(SetupArgs a, string password)
    {
        var account = ResolveAccount(a.Child, out var bare);
        if (account == null) return Fail($"Windows account '{a.Child}' does not exist on this PC. Create the standard account first.");
        if (IsLocalAdministrator(bare)) return Fail($"'{bare}' is an administrator. Tether cannot hold back an administrator; make the account a standard user first.");

        Console.WriteLine("Enrolling with the server...");
        var api = new TetherApi(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new Uri(a.Server), () => null);
        EnrollResult enrolled;
        try { enrolled = await api.EnrollAsync(a.Email, password, Environment.MachineName); }
        catch (Exception e) { return Fail($"Enrolment failed: {e.Message}"); }

        try
        {
            // A reinstall finds the adapters already pointing at the proxy, so keep the DNS captured the first time.
            var previous = TetherConfig.Load();
            var upstream = AdapterDns.CurrentUpstream();
            if (upstream.Length == 0) upstream = previous?.OriginalDns is { Length: > 0 } p ? p : new[] { "1.1.1.1", "1.0.0.1" };

            StopService();
            KillHelpers();
            Console.WriteLine("Copying files...");
            Directory.CreateDirectory(InstallDir);
            WritePayload("Tether.Service.exe", ServiceExe);
            WritePayload("Tether.Helper.exe", HelperExe);
            File.Copy(Environment.ProcessPath!, SetupExe, true);

            Console.WriteLine("Writing config...");
            new TetherConfig(a.Server.TrimEnd('/'), enrolled.Token, enrolled.DeviceId, bare, upstream).Save();
            new HelperConfig(bare).Save();
            LockDownConfig();

            Console.WriteLine("Registering service...");
            RegisterService();
            Console.WriteLine("Registering logon task for " + account + "...");
            RegisterTask(account);
            Console.WriteLine("Applying browser policy and firewall rule...");
            BrowserPolicy.Apply();
            Defender("Add-MpPreference -ExclusionPath '" + InstallDir + "'", "Defender exclusion");

            Run("sc.exe", "start", ServiceCommands.Name);
            Console.WriteLine($"Done. Device {enrolled.DeviceId} is enrolled. The service points this PC's DNS at itself within a minute; '{bare}' gets the tray helper at next sign-in.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Install failed: " + e.Message + "\nRolling back.");
            Uninstall();
            return 1;
        }
    }

    public static int Uninstall()
    {
        // The service goes first: its guard loop would point the adapters back at the proxy right after we restore them.
        StopService();
        AdapterDns.RestoreDhcp();
        Console.WriteLine("Adapter DNS restored to DHCP.");
        KillHelpers();
        Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        Run("sc.exe", "delete", ServiceCommands.Name);
        BrowserPolicy.Remove();
        Defender("Remove-MpPreference -ExclusionPath '" + InstallDir + "'", "Defender exclusion");
        TryDelete(TetherConfig.Dir);
        RemoveInstallDir();
        Console.WriteLine("Tether removed. Delete the device from the dashboard to stop offline alerts.");
        return 0;
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    private static string? ResolveAccount(string user, out string bare)
    {
        bare = user.Contains('\\') ? user[(user.IndexOf('\\') + 1)..] : user;
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(user.Contains('\\') ? user : Environment.MachineName + "\\" + user).Translate(typeof(SecurityIdentifier));
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException) { return null; }
    }

    private static bool IsLocalAdministrator(string bare)
    {
        var o = Capture("net.exe", "localgroup", "Administrators");
        return o.Split('\n').Any(l => string.Equals(l.Trim(), bare, StringComparison.OrdinalIgnoreCase));
    }

    private static void WritePayload(string resource, string dest)
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (res != null) { using var f = File.Create(dest); res.CopyTo(f); return; }
        var beside = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, resource);
        if (!File.Exists(beside)) throw new FileNotFoundException($"{resource} is not embedded in this setup and not found beside it");
        File.Copy(beside, dest, true);
    }

    /// <summary>The config directory is read-only for Users (the helper reads helper.json); config.json holds the token and is SYSTEM and Administrators only.</summary>
    private static void LockDownConfig()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var dir = new DirectoryInfo(TetherConfig.Dir);
        var ds = dir.GetAccessControl();
        ds.SetAccessRuleProtection(true, false);
        foreach (var r in ds.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList()) ds.RemoveAccessRuleAll(r);
        ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(ds);

        var file = new FileInfo(TetherConfig.SecretPath);
        var fs = file.GetAccessControl();
        fs.SetAccessRuleProtection(true, false);
        foreach (var r in fs.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList()) fs.RemoveAccessRuleAll(r);
        fs.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        fs.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(fs);
    }

    private static void RegisterService()
    {
        // The path is quoted inside the value, or a path with spaces becomes an unquoted-service-path hole.
        var bin = "\"" + ServiceExe + "\"";
        Run("sc.exe", "delete", ServiceCommands.Name);
        Require(Run("sc.exe", "create", ServiceCommands.Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Tether Service"), "sc create");
        Run("sc.exe", "description", ServiceCommands.Name, "Tether parental control service");
        Require(Run("sc.exe", ServiceCommands.Failure(ServiceCommands.Name)), "sc failure");
        Run("sc.exe", "failureflag", ServiceCommands.Name, "1");
    }

    private static void RegisterTask(string account)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"tether-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, TaskXml.Build(account, HelperExe), Encoding.Unicode);
            Require(Run("schtasks.exe", "/Create", "/TN", TaskName, "/XML", xml, "/F"), "schtasks create");
        }
        finally { TryDelete(xml); }
    }

    private static void Defender(string command, string what)
    {
        try { if (Run("powershell.exe", "-NoProfile", "-Command", command) != 0) Console.WriteLine($"Note: could not change {what}; Defender may flag the helper."); }
        catch (Exception) { Console.WriteLine($"Note: could not change {what}."); }
    }

    private static void StopService()
    {
        Run("sc.exe", "stop", ServiceCommands.Name);
        for (var i = 0; i < 20; i++)
        {
            if (!Capture("sc.exe", "query", ServiceCommands.Name).Contains("RUNNING")) return;
            Thread.Sleep(500);
        }
        foreach (var p in Process.GetProcessesByName("Tether.Service")) { try { p.Kill(); } catch (Exception) { } }
    }

    private static void KillHelpers()
    {
        foreach (var p in Process.GetProcessesByName("Tether.Helper")) { try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
    }

    /// <summary>Setup may be running from the install folder, so the folder is removed by a detached command after this process exits.</summary>
    private static void RemoveInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return;
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add($"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"");
        Process.Start(psi);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { Console.WriteLine($"Note: could not remove {path}: {e.Message}"); }
    }

    private static void Require(int code, string what) { if (code != 0) throw new InvalidOperationException($"{what} failed with exit code {code}"); }

    private static int Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string Capture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}
