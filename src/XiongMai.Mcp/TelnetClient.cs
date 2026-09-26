using System.Net.Sockets;
using System.Text;

namespace XiongMai.Mcp;

/// <summary>
/// Minimal Telnet client for the XiongMai DVR BusyBox root shell (TCP/23).
/// Handles IAC option negotiation, drives the login:/Password: prompts, and runs
/// commands using a split-sentinel marker so the shell's echo of the command line
/// does not itself trigger completion.
/// </summary>
public sealed class TelnetClient : IDisposable
{
    private const byte IAC = 255, DO = 253, WILL = 251, WONT = 252, DONT = 254, SB = 250, SE = 240;
    private const string Mark = "__XM_DONE_5C21__";
    private const string EchoArg = "__XM\"\"_DONE_5C21__";

    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly int _timeoutMs;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly StringBuilder _buf = new();
    private enum Stage { Login, Pass, Ready }
    private Stage _stage = Stage.Login;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<string>? _pending;
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;

    public TelnetClient(string host, int port = 23, string user = "root", string password = "xc3511", int timeoutMs = 12000)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _timeoutMs = timeoutMs;
    }

    public bool Connected => _tcp is { Connected: true };

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_timeoutMs);
            await _tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        }
        _stream = _tcp.GetStream();
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_loopCts.Token));

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readyCts.CancelAfter(_timeoutMs);
        await using (readyCts.Token.Register(() => _ready.TrySetException(new TimeoutException("telnet login timeout"))))
            await _ready.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var raw = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                var n = await _stream.ReadAsync(raw.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0) break;
                var text = StripIac(raw.AsSpan(0, n));
                lock (_lock)
                {
                    _buf.Append(text);
                    ProcessBuffer();
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception e)
        {
            _ready?.TrySetException(e);
            _pending?.TrySetException(e);
        }
    }

    private string StripIac(ReadOnlySpan<byte> chunk)
    {
        var reply = new List<byte>();
        var outp = new StringBuilder();
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] == IAC && i + 2 < chunk.Length)
            {
                var cmd = chunk[i + 1];
                var opt = chunk[i + 2];
                if (cmd == DO) { reply.AddRange(new byte[] { IAC, WONT, opt }); i += 2; }
                else if (cmd == WILL) { reply.AddRange(new byte[] { IAC, DONT, opt }); i += 2; }
                else if (cmd == SB) { while (i < chunk.Length && chunk[i] != SE) i++; }
                else { i += 2; }
            }
            else if (chunk[i] != IAC)
            {
                outp.Append((char)chunk[i]);
            }
        }
        if (reply.Count > 0 && _stream is not null)
            _stream.Write(reply.ToArray(), 0, reply.Count);
        return outp.ToString();
    }

    // Called under _lock.
    private void ProcessBuffer()
    {
        var s = _buf.ToString();
        var tail = s.Length > 80 ? s[^80..] : s;
        var tailLower = tail.ToLowerInvariant();

        if (_stage == Stage.Login && tailLower.Contains("login:"))
        {
            _stage = Stage.Pass;
            Write(_user + "\r\n");
            _buf.Clear();
            return;
        }
        if (_stage == Stage.Pass && tailLower.Contains("password:"))
        {
            _stage = Stage.Ready;
            Write(_password + "\r\n");
            _buf.Clear();
            _ = Task.Run(async () => { await Task.Delay(1500); _ready?.TrySetResult(true); });
            return;
        }
        if (_stage == Stage.Ready && _pending is not null && s.Contains(Mark))
        {
            var idx = s.IndexOf(Mark, StringComparison.Ordinal);
            var output = s[..idx];
            var nl = output.IndexOf('\n');
            if (nl >= 0) output = output[(nl + 1)..];
            output = output.Replace("\r", "");
            var p = _pending;
            _pending = null;
            _buf.Clear();
            p.TrySetResult(output.TrimStart('\n').Trim());
        }
    }

    private void Write(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        _stream?.Write(bytes, 0, bytes.Length);
    }

    public async Task<string> ExecAsync(string command, int timeoutMs = 12000, CancellationToken ct = default)
    {
        if (_stream is null || _stage != Stage.Ready) throw new InvalidOperationException("telnet not ready");
        TaskCompletionSource<string> tcs;
        lock (_lock)
        {
            if (_pending is not null) throw new InvalidOperationException("a command is already in progress");
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            _buf.Clear();
            Write($"{command}; echo {EchoArg}\r\n");
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await using (cts.Token.Register(() => { lock (_lock) { if (_pending == tcs) _pending = null; } tcs.TrySetException(new TimeoutException("command timeout")); }))
            return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<string> ExecAllAsync(IEnumerable<string> commands, CancellationToken ct = default)
    {
        var outp = new StringBuilder();
        foreach (var c in commands)
            outp.Append($"$ {c}\n{await ExecAsync(c, ct: ct)}\n");
        return outp.ToString();
    }

    /// <summary>Send a command without waiting for output (e.g. a reboot that drops the link).</summary>
    public void FireAndForget(string command)
    {
        if (_stream is not null && _stage == Stage.Ready) Write($"{command}\r\n");
    }

    public void Close()
    {
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        try { if (_stream is not null) Write("exit\r\n"); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        _stage = Stage.Login;
        _buf.Clear();
        _pending = null;
    }

    public void Dispose() => Close();
}
