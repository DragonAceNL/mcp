using System.Management.Automation;
using Mcp.Common.Text;

namespace Windows.Mcp;

/// <summary>
/// High-level operations against one Windows server over PowerShell Remoting.
/// Caller-supplied values are passed as bound parameters, never interpolated
/// into script text, so there is no command-injection surface.
/// </summary>
public sealed class WindowsClient
{
    private readonly WinRmSession _session;

    public WindowsClient(WindowsServerConfig cfg) => _session = new WinRmSession(cfg);

    public string Name => _session.Name;
    public string Host => _session.Host;

    public void Disconnect() => _session.Disconnect();

    // ── General ────────────────────────────────────────────────────────────

    public async Task<string> SystemInfoAsync(CancellationToken ct)
    {
        const string script = @"
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
[pscustomobject]@{
  ComputerName = $env:COMPUTERNAME
  Domain       = $cs.Domain
  OS           = $os.Caption
  Version      = $os.Version
  Uptime       = (Get-Date) - $os.LastBootUpTime
  Roles        = (Get-WindowsFeature -ErrorAction SilentlyContinue | Where-Object Installed | Where-Object { $_.FeatureType -eq 'Role' } | Select-Object -ExpandProperty Name) -join ', '
  MemoryFreeGB = [math]::Round($os.FreePhysicalMemory/1MB,1)
  MemoryTotGB  = [math]::Round($os.TotalVisibleMemorySize/1MB,1)
} | Format-List";
        return Text(await _session.InvokeAsync(ps => ps.AddScript(script), json: false, ct));
    }

    public async Task<string> RunPowerShellAsync(string script, bool json, CancellationToken ct)
        => Text(await _session.InvokeAsync(ps => ps.AddScript(script), json, ct));

    // ── Services ───────────────────────────────────────────────────────────

    public async Task<string> GetServiceAsync(string? name, CancellationToken ct)
    {
        return Text(await _session.InvokeAsync(ps =>
        {
            ps.AddCommand("Get-Service");
            if (!string.IsNullOrEmpty(name))
            {
                Validate.Require(name, Validate.IsServiceName, $"invalid service name: {name}");
                ps.AddParameter("Name", name);
            }
            ps.AddCommand("Select-Object")
              .AddParameter("Property", new[] { "Status", "Name", "DisplayName", "StartType" });
        }, json: false, ct));
    }

    public async Task<string> ServiceActionAsync(string name, string action, CancellationToken ct)
    {
        Validate.Require(name, Validate.IsServiceName, $"invalid service name: {name}");
        var cmd = action.ToLowerInvariant() switch
        {
            "start" => "Start-Service",
            "stop" => "Stop-Service",
            "restart" => "Restart-Service",
            _ => throw new ModelContextProtocol.McpException($"invalid action: {action} (start|stop|restart)"),
        };
        var r = await _session.InvokeAsync(ps =>
        {
            ps.AddCommand(cmd).AddParameter("Name", name).AddParameter("PassThru", true);
            if (cmd is "Stop-Service" or "Restart-Service") ps.AddParameter("Force", true);
            ps.AddCommand("Select-Object").AddParameter("Property", new[] { "Status", "Name", "DisplayName" });
        }, json: false, ct);
        return Text(r);
    }

    // ── Event log ──────────────────────────────────────────────────────────

    public async Task<string> EventLogAsync(string logName, int maxEvents, string? level, CancellationToken ct)
    {
        Validate.Require(logName, Validate.IsEventLogName, $"invalid log name: {logName}");
        var count = Math.Clamp(maxEvents, 1, 200);
        return Text(await _session.InvokeAsync(ps =>
        {
            var filter = new System.Collections.Hashtable { ["LogName"] = logName };
            if (!string.IsNullOrEmpty(level))
            {
                var lvl = level.ToLowerInvariant() switch
                {
                    "critical" => 1, "error" => 2, "warning" => 3, "information" => 4, "verbose" => 5,
                    _ => throw new ModelContextProtocol.McpException($"invalid level: {level}"),
                };
                filter["Level"] = lvl;
            }
            ps.AddCommand("Get-WinEvent").AddParameter("FilterHashtable", filter).AddParameter("MaxEvents", count);
            ps.AddCommand("Select-Object")
              .AddParameter("Property", new[] { "TimeCreated", "Id", "LevelDisplayName", "ProviderName", "Message" });
            ps.AddCommand("Format-List");
        }, json: false, ct));
    }

    // ── Active Directory ─────────────────────────────────────────────────────

    public async Task<string> AdGetUserAsync(string identity, CancellationToken ct)
    {
        Validate.Require(identity, Validate.IsSamAccount, $"invalid account identity: {identity}");
        return Text(await _session.InvokeAsync(ps =>
        {
            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", identity)
              .AddParameter("Properties", new[] { "LockedOut", "Enabled", "LastLogonDate", "PasswordLastSet", "PasswordExpired", "EmailAddress", "MemberOf" });
            ps.AddCommand("Select-Object")
              .AddParameter("Property", new[] { "SamAccountName", "Name", "Enabled", "LockedOut", "PasswordExpired", "PasswordLastSet", "LastLogonDate", "EmailAddress" });
            ps.AddCommand("Format-List");
        }, json: false, ct));
    }

    public async Task<string> AdListLockedAsync(CancellationToken ct)
        => Text(await _session.InvokeAsync(ps =>
        {
            ps.AddCommand("Search-ADAccount").AddParameter("LockedOut", true);
            ps.AddCommand("Select-Object")
              .AddParameter("Property", new[] { "SamAccountName", "Name", "LockedOut", "LastLogonDate" });
            ps.AddCommand("Format-Table").AddParameter("AutoSize", true);
        }, json: false, ct));

    public async Task<string> AdUnlockAsync(string identity, CancellationToken ct)
    {
        Validate.Require(identity, Validate.IsSamAccount, $"invalid account identity: {identity}");
        var r = await _session.InvokeAsync(ps =>
            ps.AddCommand("Unlock-ADAccount").AddParameter("Identity", identity), json: false, ct);
        return r.Error is null ? $"Unlocked {identity}" : Text(r);
    }

    public async Task<string> AdResetPasswordAsync(string identity, string newPassword, bool changeAtLogon, CancellationToken ct)
    {
        Validate.Require(identity, Validate.IsSamAccount, $"invalid account identity: {identity}");
        const string script = @"
param($id, $pw, $change)
Set-ADAccountPassword -Identity $id -Reset -NewPassword (ConvertTo-SecureString $pw -AsPlainText -Force)
if ($change) { Set-ADUser -Identity $id -ChangePasswordAtLogon $true }
""Password reset for $id""";
        var r = await _session.InvokeAsync(ps => ps.AddScript(script)
            .AddParameter("id", identity)
            .AddParameter("pw", newPassword)
            .AddParameter("change", changeAtLogon), json: false, ct);
        return Text(r);
    }

    private static string Text(PsResult r)
        => r.Error is null
            ? (string.IsNullOrWhiteSpace(r.Text) ? "(no output)" : r.Text)
            : string.IsNullOrWhiteSpace(r.Text) ? $"ERROR: {r.Error}" : $"{r.Text}\n\nERROR: {r.Error}";
}
