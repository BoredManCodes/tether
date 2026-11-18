using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace Tether.Service;

internal static class Netsh
{
    public static int Run(params string[] args)
    {
        var psi = new ProcessStartInfo("netsh") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch (Exception) { } return -1; }
        return p.ExitCode;
    }
}

/// <summary>Points every active adapter's DNS at the local proxy, and puts it back to DHCP on uninstall.</summary>
public static class AdapterDns
{
    private static IEnumerable<NetworkInterface> Active() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.Supports(NetworkInterfaceComponent.IPv4));

    private static bool OnlyLoopback(NetworkInterface n)
    {
        var dns = n.GetIPProperties().DnsAddresses;
        return dns.Count > 0 && dns.All(IPAddress.IsLoopback);
    }

    /// <summary>The DNS servers in use before Tether took over; loopback is never returned.</summary>
    public static string[] CurrentUpstream() => Active()
        .SelectMany(n => n.GetIPProperties().DnsAddresses)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Select(a => a.ToString()).Distinct().ToArray();

    public static bool AllPointedAtProxy() => Active().All(OnlyLoopback);

    /// <summary>Touches only adapters that are not already pointed at the proxy, so a steady state costs nothing.</summary>
    public static void EnsureLoopback()
    {
        foreach (var n in Active().Where(n => !OnlyLoopback(n)))
        {
            Netsh.Run("interface", "ip", "set", "dns", $"name={n.Name}", "static", "127.0.0.1", "primary", "validate=no");
            if (n.Supports(NetworkInterfaceComponent.IPv6))
                Netsh.Run("interface", "ipv6", "set", "dnsservers", $"name={n.Name}", "static", "::1", "primary", "validate=no");
        }
    }

    public static void RestoreDhcp()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            Netsh.Run("interface", "ip", "set", "dns", $"name={n.Name}", "source=dhcp");
            Netsh.Run("interface", "ipv6", "set", "dnsservers", $"name={n.Name}", "source=dhcp");
        }
    }
}

/// <summary>Turns off DNS-over-HTTPS in Chrome, Edge and Firefox by policy and blocks outbound DNS over TLS.</summary>
public static class BrowserPolicy
{
    private const string FirewallRule = "Tether block DNS over TLS";

    private static readonly (string Key, string Name, object Value, RegistryValueKind Kind)[] Values =
    {
        (@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off", RegistryValueKind.String),
        (@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off", RegistryValueKind.String),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0, RegistryValueKind.DWord),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1, RegistryValueKind.DWord),
    };

    public static void Apply()
    {
        foreach (var (key, name, value, kind) in Values)
        {
            using var k = Registry.LocalMachine.CreateSubKey(key, true);
            if (!Equals(k.GetValue(name), value)) k.SetValue(name, value, kind);
        }
        if (!RuleExists())
            Netsh.Run("advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=out", "action=block", "protocol=TCP", "remoteport=853");
    }

    public static bool IsIntact()
    {
        foreach (var (key, name, value, _) in Values)
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k == null || !Equals(k.GetValue(name), value)) return false;
        }
        return RuleExists();
    }

    private static bool RuleExists() => Netsh.Run("advfirewall", "firewall", "show", "rule", $"name={FirewallRule}") == 0;

    public static void Remove()
    {
        foreach (var (key, name, _, _) in Values)
        {
            using var k = Registry.LocalMachine.OpenSubKey(key, true);
            k?.DeleteValue(name, false);
        }
        Netsh.Run("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
    }
}
