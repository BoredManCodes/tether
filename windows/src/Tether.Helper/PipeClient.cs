using System.IO.Pipes;
using System.Text;
using Tether.Shared;

namespace Tether.Helper;

/// <summary>Client end of the service pipe. Reconnects forever; sends are dropped while disconnected.</summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public event Action<PipeMsg>? Received;

    public PipeClient(string name) => _name = name;

    public bool Connected { get { lock (_lock) return _writer != null; } }

    public void Start() => Task.Run(Loop);

    public void Send(PipeMsg msg)
    {
        lock (_lock)
        {
            try { _writer?.WriteLine(msg.ToLine()); }
            catch (IOException) { _writer = null; }
        }
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, _cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                lock (_lock) _writer = writer;
                Send(new PipeMsg("hello"));
                string? line;
                while ((line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) != null)
                {
                    var m = PipeMsg.FromLine(line);
                    if (m != null) Received?.Invoke(m);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* service not up yet or pipe broke: retry */ }
            finally { lock (_lock) _writer = null; }
            try { await Task.Delay(2000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _cts.Cancel();
}
