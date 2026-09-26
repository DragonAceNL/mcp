using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Windows.Mcp;

[McpServerToolType]
public sealed class WindowsTools(WindowsManager manager)
{
    private const string ServerDesc = "Which server to target (name or host). Optional if only one server is configured.";

    [McpServerTool(Name = "windows_list_servers")]
    [Description("List the configured Windows servers (names and hosts) this server manages")]
    public string ListServers()
    {
        var list = manager.List();
        return list.Count > 0 ? string.Join('\n', list.Select(s => $"{s.Name} — {s.Host}")) : "No servers configured";
    }

    [McpServerTool(Name = "windows_system_info")]
    [Description("Get OS, domain, uptime, installed roles, and memory for a server")]
    public Task<string> SystemInfo([Description(ServerDesc)] string? server = null, CancellationToken ct = default)
        => manager.Get(server).SystemInfoAsync(ct);

    [McpServerTool(Name = "windows_get_service")]
    [Description("List Windows services and their status; optionally a single service by name")]
    public Task<string> GetService(
        [Description("Service name (optional; all if omitted)")] string? name = null,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
        => manager.Get(server).GetServiceAsync(name, ct);

    [McpServerTool(Name = "windows_service_action")]
    [Description("Start, stop, or restart a Windows service. Destructive: requires confirm=true.")]
    public Task<string> ServiceAction(
        [Description("Service name")] string name,
        [Description("start | stop | restart")] string action,
        [Description("Must be true to actually perform the action")] bool confirm = false,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
    {
        if (!confirm)
            return Task.FromResult($"Refusing: this would {action} service \"{name}\". Re-run with confirm=true.");
        return manager.Get(server).ServiceActionAsync(name, action, ct);
    }

    [McpServerTool(Name = "windows_event_log")]
    [Description("Read recent Windows event-log entries (e.g. System, Application, Security)")]
    public Task<string> EventLog(
        [Description("Log name (e.g. System, Application, Security)")] string logName,
        [Description("Max events to return (1-200, default 20)")] int maxEvents = 20,
        [Description("Optional level filter: Critical|Error|Warning|Information|Verbose")] string? level = null,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
        => manager.Get(server).EventLogAsync(logName, maxEvents, level, ct);

    [McpServerTool(Name = "windows_run_powershell")]
    [Description("Run an arbitrary PowerShell script on the server and return its output. Powerful/admin-level; use for read-only queries or when no dedicated tool exists.")]
    public Task<string> RunPowerShell(
        [Description("PowerShell script text to execute remotely")] string script,
        [Description("Return output as JSON instead of formatted text (default false)")] bool json = false,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
        => manager.Get(server).RunPowerShellAsync(script, json, ct);

    // ── Active Directory (domain controller) ─────────────────────────────────

    [McpServerTool(Name = "ad_get_user")]
    [Description("Get an Active Directory user's status (enabled, locked, password info). Target a domain controller.")]
    public Task<string> AdGetUser(
        [Description("SamAccountName of the user")] string identity,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
        => manager.Get(server).AdGetUserAsync(identity, ct);

    [McpServerTool(Name = "ad_list_locked_accounts")]
    [Description("List Active Directory accounts that are currently locked out")]
    public Task<string> AdListLocked([Description(ServerDesc)] string? server = null, CancellationToken ct = default)
        => manager.Get(server).AdListLockedAsync(ct);

    [McpServerTool(Name = "ad_unlock_account")]
    [Description("Unlock a locked-out Active Directory account. Destructive: requires confirm=true.")]
    public Task<string> AdUnlock(
        [Description("SamAccountName of the user")] string identity,
        [Description("Must be true to actually unlock")] bool confirm = false,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
    {
        if (!confirm)
            return Task.FromResult($"Refusing: this would unlock account \"{identity}\". Re-run with confirm=true.");
        return manager.Get(server).AdUnlockAsync(identity, ct);
    }

    [McpServerTool(Name = "ad_reset_password")]
    [Description("Reset an Active Directory user's password. Destructive: requires confirm=true.")]
    public Task<string> AdResetPassword(
        [Description("SamAccountName of the user")] string identity,
        [Description("The new password")] string newPassword,
        [Description("Force the user to change the password at next logon (default true)")] bool changeAtLogon = true,
        [Description("Must be true to actually reset the password")] bool confirm = false,
        [Description(ServerDesc)] string? server = null,
        CancellationToken ct = default)
    {
        if (!confirm)
            return Task.FromResult($"Refusing: this would reset the password for \"{identity}\". Re-run with confirm=true.");
        return manager.Get(server).AdResetPasswordAsync(identity, newPassword, changeAtLogon, ct);
    }
}
