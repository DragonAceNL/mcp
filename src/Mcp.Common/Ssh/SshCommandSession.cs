using System.Text;
using Renci.SshNet;

namespace Mcp.Common.Ssh;

public sealed class SshOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? Password { get; init; }
    /// <summary>Inline private key (PEM/OpenSSH) contents.</summary>
    public string? PrivateKey { get; init; }
    /// <summary>Path to a private key file (PEM/OpenSSH).</summary>
    public string? PrivateKeyPath { get; init; }
    /// <summary>Enable legacy KEX/cipher/host-key algorithms for old dropbear (Tomato).</summary>
    public bool AllowLegacyAlgorithms { get; init; }
    /// <summary>
    /// When true (EdgeRouter), a non-zero exit with stderr throws. When false
    /// (Tomato), only throw when exit != 0 AND stderr present AND no stdout;
    /// otherwise stderr is appended to stdout.
    /// </summary>
    public bool StrictExit { get; init; } = true;
}

/// <summary>
/// A single persistent, serialised SSH connection. Mirrors the proven TS design
/// (EdgeRouter/Tomato): one live connection reused across commands, all commands
/// serialised so two never run at once (the devices treat concurrency as abuse),
/// and an idle timer that disconnects after 30 s of inactivity.
/// </summary>
public sealed class SshCommandSession : IDisposable
{
    private readonly SshOptions _opts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);
    private SshClient? _client;
    private Timer? _idleTimer;
    private bool _disposed;

    public SshCommandSession(SshOptions opts) => _opts = opts;

    public string Host => _opts.Host;

    private SshClient GetClient()
    {
        if (_client is { IsConnected: true }) return _client;
        _client?.Dispose();
        _client = new SshClient(BuildConnectionInfo());
        _client.KeepAliveInterval = TimeSpan.FromSeconds(10);
        _client.Connect();
        return _client;
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();
        if (_opts.Password is not null)
            methods.Add(new PasswordAuthenticationMethod(_opts.Username, _opts.Password));
        if (_opts.PrivateKey is not null || _opts.PrivateKeyPath is not null)
        {
            PrivateKeyFile keyFile = _opts.PrivateKeyPath is not null
                ? new PrivateKeyFile(_opts.PrivateKeyPath)
                : new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(_opts.PrivateKey!)));
            DeprioritizeSha1(keyFile);
            methods.Add(new PrivateKeyAuthenticationMethod(_opts.Username, keyFile));
        }
        if (methods.Count == 0)
            throw new InvalidOperationException("no SSH authentication configured (password or private key required)");

        var ci = new ConnectionInfo(_opts.Host, _opts.Port, _opts.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        return ci;
    }

    /// <summary>
    /// Move the deprecated SHA-1 <c>ssh-rsa</c> signature to the END of an RSA
    /// key's algorithm list so modern servers negotiate <c>rsa-sha2-256/512</c>
    /// first. Critical for FreshTomato's dropbear, which ACCEPTS the ssh-rsa key
    /// probe but ABORTS the connection when the client actually signs with SHA-1.
    /// Ancient SHA-1-only servers still work because ssh-rsa stays as a fallback.
    /// </summary>
    private static void DeprioritizeSha1(PrivateKeyFile keyFile)
    {
        if (keyFile.HostKeyAlgorithms is not System.Collections.IList list) return;
        var moved = new List<object>();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i]!;
            var name = item.GetType().GetProperty("Name")?.GetValue(item) as string;
            if (name == "ssh-rsa")
            {
                moved.Add(item);
                list.RemoveAt(i);
            }
        }
        foreach (var item in moved) list.Add(item); // append at the end
    }

    private void ResetIdleTimer()
    {
        _idleTimer ??= new Timer(_ => Disconnect(), null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
    }

    public void Disconnect()
    {
        lock (this)
        {
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_client is not null)
            {
                try { if (_client.IsConnected) _client.Disconnect(); } catch { /* ignore */ }
                _client.Dispose();
                _client = null;
            }
        }
    }

    /// <summary>Run one command over the shared, serialised connection.</summary>
    public async Task<string> ExecAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetClient();
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2);
            var stdout = await Task.Factory.FromAsync(cmd.BeginExecute(), cmd.EndExecute).ConfigureAwait(false);
            var stderr = cmd.Error;
            var code = cmd.ExitStatus ?? 0;
            ResetIdleTimer();

            if (_opts.StrictExit)
            {
                if (code != 0 && !string.IsNullOrEmpty(stderr))
                    throw new InvalidOperationException($"Command failed (exit code {code}): {stderr}");
                return (stdout ?? string.Empty).Trim();
            }
            else
            {
                if (code != 0 && !string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
                    throw new InvalidOperationException($"Command failed (exit code {code}): {stderr}");
                var combined = (stdout ?? string.Empty) + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr);
                return combined.Trim();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run a sequence of lines inside an interactive login shell on a PTY — the
    /// C# equivalent of the TS <c>vbash --login -i</c> path. Used by EdgeRouter
    /// configure(): EdgeOS's firewall commit hook only classifies new rulesets
    /// correctly from a genuine login+tty session.
    /// </summary>
    public async Task<string> ExecLoginShellAsync(IReadOnlyList<string> lines, string shellCommand, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = GetClient();
            var output = new StringBuilder();
            using var shell = client.CreateShellStream("vt100", 200, 50, 800, 600, 8192);
            // Launch the interactive login shell inside the PTY.
            shell.WriteLine(shellCommand);
            foreach (var line in lines)
                shell.WriteLine(line);

            // Completion is detected by a sentinel echoed AFTER the caller's lines
            // (which include commit/save) but BEFORE we exit the shell. A short idle
            // window is unreliable: a `commit` is several seconds of silence and
            // would end the read early, releasing the gate mid-commit and letting
            // the next config session clobber it. The '""' splits the marker so the
            // shell's echo of the command line does not itself contain the sentinel.
            const string marker = "__MCP_SHELL_DONE_7F3A__";
            shell.WriteLine("echo __MCP\"\"_SHELL_DONE_7F3A__");
            shell.WriteLine("exit");

            var start = DateTime.UtcNow;
            while (true)
            {
                var chunk = shell.Read();
                if (!string.IsNullOrEmpty(chunk))
                {
                    output.Append(chunk);
                    if (output.ToString().Contains(marker)) break; // commit + save finished
                }
                else
                {
                    if (!shell.CanRead) break;
                    if (DateTime.UtcNow - start > TimeSpan.FromSeconds(180)) break; // hard safety cap
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
            ResetIdleTimer();
            return StripAnsi(output.ToString()).Replace(marker, "").TrimEnd();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StripAnsi(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;?]*[A-Za-z]", string.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _idleTimer?.Dispose();
        _gate.Dispose();
    }
}
