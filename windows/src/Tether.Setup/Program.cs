using System.Text;
using Tether.Setup;

var a = SetupArgs.Parse(args);
if (a.Error != null) { Console.Error.WriteLine(a.Error); return 2; }
if (!Installer.IsAdmin()) { Console.Error.WriteLine("Run this from an administrator account (right-click, Run as administrator)."); return 1; }

if (a.Command == "uninstall") return Installer.Uninstall();

Console.Write($"Password for {a.Email} on {a.Server}: ");
var pw = new StringBuilder();
for (ConsoleKeyInfo k; (k = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
{
    if (k.Key == ConsoleKey.Backspace) { if (pw.Length > 0) pw.Length--; }
    else if (!char.IsControl(k.KeyChar)) pw.Append(k.KeyChar);
}
Console.WriteLine();
return await Installer.InstallAsync(a, pw.ToString());
