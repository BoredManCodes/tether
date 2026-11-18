using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Tether.Shared;

namespace Tether.Service;

/// <summary>
/// Server end of the helper pipe. Only SYSTEM, Administrators and the configured child account can
/// open it, so another local user cannot feed the service fake hits or frames.
/// </summary>
public sealed class PipeHost
{
    private readonly string _name;
    private readonly string _childUser;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public event Action<PipeMsg>? Received;
    public event Action? HelperConnected;

    public PipeHost(string name, string childUser, ILogger log)
    {
        _name = name;
        _childUser = childUser;
        _log = log;
    }

    public bool Connected { get { lock (_lock) return _writer != null; } }

    public void Send(PipeMsg msg)
    {
        lock (_lock)
        {
            try { _writer?.WriteLine(msg.ToLine()); }
            catch (IOException) { _writer = null; }
        }
    }

    private PipeSecurity BuildSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new NTAccount(_childUser), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, BuildSecurity());
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                lock (_lock) _writer = writer;
                _log.LogInformation("helper connected");
                HelperConnected?.Invoke();
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    var m = PipeMsg.FromLine(line);
                    if (m != null) Received?.Invoke(m);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log.LogWarning(e, "helper pipe error"); }
            finally { lock (_lock) _writer = null; }
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
