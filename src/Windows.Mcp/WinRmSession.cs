using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;

namespace Windows.Mcp;

/// <summary>A configured Windows target reachable over WinRM / PowerShell Remoting.</summary>
public sealed record WindowsServerConfig(
    string Name,
    string Host,
    int Port,
    bool UseSsl,
    string? Username,
    string? Password,
    string Auth,
    string? ConfigurationName,
    bool SkipCertCheck);

/// <summary>Result of a remote PowerShell invocation.</summary>
public sealed record PsResult(string Text, string? Error);

/// <summary>
/// A single persistent, serialised PowerShell Remoting (PSRP over WinRM) session.
/// Mirrors <c>SshCommandSession</c>: one live runspace reused across calls, all
/// calls serialised so two never run at once, and an idle timer that closes the
/// runspace after inactivity. Kept inside this project so the heavy PowerShell SDK
/// dependency never leaks into the shared <c>Mcp.Common</c> library.
/// </summary>
public sealed class WinRmSession : IDisposable
{
    private readonly WindowsServerConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(60);
    private Runspace? _runspace;
    private Timer? _idleTimer;
    private bool _disposed;

    public WinRmSession(WindowsServerConfig cfg) => _cfg = cfg;

    public string Name => _cfg.Name;
    public string Host => _cfg.Host;

    /// <summary>
    /// Invoke a pipeline against the remote runspace. <paramref name="configure"/>
    /// builds the command(s) using AddCommand/AddParameter/AddScript so caller input
    /// is passed as bound parameters (never string-interpolated into a script).
    /// Output is rendered as text, or JSON when <paramref name="json"/> is true.
    /// </summary>
    public async Task<PsResult> InvokeAsync(Action<PowerShell> configure, bool json = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var rs = GetRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                configure(ps);
                if (json)
                    ps.AddCommand("ConvertTo-Json").AddParameter("Depth", 5);
                else
                    ps.AddCommand("Out-String").AddParameter("Width", 240);

                var output = ps.Invoke();
                var text = string.Concat(output.Select(o => o?.ToString())).TrimEnd();
                var error = ps.HadErrors && ps.Streams.Error.Count > 0
                    ? string.Join("\n", ps.Streams.Error.Select(e => e.ToString()))
                    : null;
                ResetIdle();
                return new PsResult(text, error);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Runspace GetRunspace()
    {
        if (_runspace is { RunspaceStateInfo.State: RunspaceState.Opened }) return _runspace;
        DisposeRunspace();
        _runspace = RunspaceFactory.CreateRunspace(BuildConnectionInfo());
        _runspace.Open();
        return _runspace;
    }

    private WSManConnectionInfo BuildConnectionInfo()
    {
        var scheme = _cfg.UseSsl ? "https" : "http";
        var uri = new Uri($"{scheme}://{_cfg.Host}:{_cfg.Port}/wsman");
        var shellUri = "http://schemas.microsoft.com/powershell/" +
                       (string.IsNullOrEmpty(_cfg.ConfigurationName) ? "Microsoft.PowerShell" : _cfg.ConfigurationName);

        var ci = new WSManConnectionInfo(uri, shellUri, BuildCredential())
        {
            AuthenticationMechanism = ParseAuth(_cfg.Auth),
            OpenTimeout = 30_000,
            OperationTimeout = 120_000,
            MaximumConnectionRedirectionCount = 0,
        };
        if (_cfg.SkipCertCheck)
        {
            ci.SkipCACheck = true;
            ci.SkipCNCheck = true;
            ci.SkipRevocationCheck = true;
        }
        return ci;
    }

    private PSCredential? BuildCredential()
    {
        // No username => integrated auth as the current process identity (Kerberos in-domain).
        if (string.IsNullOrEmpty(_cfg.Username)) return null;
        var sec = new SecureString();
        foreach (var c in _cfg.Password ?? "") sec.AppendChar(c);
        sec.MakeReadOnly();
        return new PSCredential(_cfg.Username, sec);
    }

    private static AuthenticationMechanism ParseAuth(string auth) => auth.ToLowerInvariant() switch
    {
        "kerberos" => AuthenticationMechanism.Kerberos,
        "basic" => AuthenticationMechanism.Basic,
        "credssp" => AuthenticationMechanism.Credssp,
        "negotiatewithimplicitcredential" => AuthenticationMechanism.NegotiateWithImplicitCredential,
        "default" => AuthenticationMechanism.Default,
        _ => AuthenticationMechanism.Negotiate,
    };

    private void ResetIdle()
    {
        _idleTimer?.Dispose();
        _idleTimer = new Timer(_ => Disconnect(), null, _idleTimeout, Timeout.InfiniteTimeSpan);
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { DisposeRunspace(); }
        finally { _gate.Release(); }
    }

    private void DisposeRunspace()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        try { _runspace?.Dispose(); } catch { /* best effort */ }
        _runspace = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeRunspace();
        _gate.Dispose();
    }
}
