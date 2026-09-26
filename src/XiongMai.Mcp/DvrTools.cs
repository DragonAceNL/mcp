using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace XiongMai.Mcp;

[McpServerToolType]
public sealed class DvrTools(DvrState state)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static string Json(object o) => JsonSerializer.Serialize(o, Indented);

    private DvripClient Require()
        => state.Client is { Connected: true, LoggedInUser: not null } c ? c : throw new McpException("Not logged in. Call dvr_login first.");

    [McpServerTool(Name = "dvr_hash")]
    [Description("Compute the XiongMai \"Sofia\" login hash for a password (diagnostic; no network access).")]
    public string Hash([Description("Plaintext password")] string password)
        => Json(new { password, sofia = DvripClient.SofiaHash(password) });

    [McpServerTool(Name = "dvr_login")]
    [Description("Connect and authenticate to the XiongMai/Sofia DVR over DVRIP (TCP/34567). Establishes a session reused by the other tools. Returns whether the credentials worked and the decoded status (e.g. 203 = wrong password, 204 = no such user, 205 = locked).")]
    public async Task<string> Login(
        [Description("DVR IP")] string? host = null,
        [Description("DVRIP port")] int? port = null,
        [Description("Username")] string? user = null,
        [Description("Password (defaults to DVR_PASSWORD env if set)")] string? password = null,
        CancellationToken ct = default)
    {
        var pw = password ?? state.Password ?? throw new McpException("No password provided and DVR_PASSWORD env is not set.");
        state.Client?.Close();
        var client = new DvripClient(host ?? state.Host, port ?? state.Port);
        var r = await client.LoginAsync(user ?? state.User, pw, ct);
        if (!r.Ok) client.Close();
        state.Client = r.Ok ? client : null;
        return Json(new { ok = r.Ok, ret = r.Ret, status = r.Message, sessionId = client.SessionIdHex });
    }

    [McpServerTool(Name = "dvr_try_passwords")]
    [Description("Test a list of candidate passwords for a user and report which one (if any) is accepted. For recovering access to your own device. Reconnects for each attempt; note the DVR may temporarily lock the account after several wrong tries (Ret 205).")]
    public async Task<string> TryPasswords(
        [Description("Candidate passwords to try in order")] string[] passwords,
        [Description("DVR IP")] string? host = null,
        [Description("DVRIP port")] int? port = null,
        [Description("Username")] string? user = null,
        CancellationToken ct = default)
    {
        var h = host ?? state.Host;
        var p = port ?? state.Port;
        var u = user ?? state.User;
        var attempts = new List<object>();
        string? found = null;
        foreach (var pw in passwords)
        {
            var c = new DvripClient(h, p);
            try
            {
                var r = await c.LoginAsync(u, pw, ct);
                attempts.Add(new { password = pw, ret = r.Ret, status = r.Message, ok = r.Ok });
                if (r.Ok) { found = pw; state.Client?.Close(); state.Client = c; break; }
                if (r.Ret is 205 or 206) { c.Close(); break; }
            }
            catch (Exception e)
            {
                attempts.Add(new { password = pw, ret = (int?)null, status = $"error: {e.Message}", ok = false });
            }
            c.Close();
        }
        return Json(new { found, user = u, attempts });
    }

    [McpServerTool(Name = "dvr_system_info")]
    [Description("Get SystemInfo (model, serial, hardware/software version, channel count, state). Requires an active login.")]
    public async Task<string> SystemInfo([Description("SystemInfo family object: SystemInfo (default), StorageInfo, or WorkState")] string? name = null, CancellationToken ct = default)
        => Json(await Require().SysInfoAsync(name ?? "SystemInfo", ct) ?? default);

    [McpServerTool(Name = "dvr_config_get")]
    [Description("Read a config node by name, e.g. \"General\", \"NetWork.NetCommon\", \"General.Location\". Requires login.")]
    public async Task<string> ConfigGet([Description("Config node name")] string name, CancellationToken ct = default)
        => Json(await Require().ConfigGetAsync(name, ct) ?? default);

    [McpServerTool(Name = "dvr_get_time")]
    [Description("Query the DVR current date/time (OPTimeQuery). Requires login.")]
    public async Task<string> GetTime(CancellationToken ct = default) => Json(await Require().GetTimeAsync(ct) ?? default);

    [McpServerTool(Name = "dvr_channel_titles")]
    [Description("Get the configured channel/camera titles. Requires login.")]
    public async Task<string> ChannelTitles(CancellationToken ct = default) => Json(await Require().GetChannelTitlesAsync(ct) ?? default);

    [McpServerTool(Name = "dvr_users")]
    [Description("List configured users and groups. Requires login.")]
    public async Task<string> Users(CancellationToken ct = default) => Json(await Require().GetUsersAsync(ct) ?? default);

    [McpServerTool(Name = "dvr_snapshot")]
    [Description("Best-effort JPEG snapshot of a channel, saved to a file. Requires login. Some firmwares return a JSON error instead of an image.")]
    public async Task<string> Snapshot(
        [Description("Absolute path to write the .jpg to")] string outputPath,
        [Description("Channel index (0-based, default 0)")] int channel = 0,
        CancellationToken ct = default)
    {
        var (jpeg, json) = await Require().SnapshotAsync(channel, ct);
        if (jpeg is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, jpeg, ct);
            return Json(new { saved = outputPath, bytes = jpeg.Length });
        }
        return Json(new { saved = (string?)null, error = "no image returned", response = json });
    }

    [McpServerTool(Name = "dvr_command")]
    [Description("Advanced escape hatch: send a raw DVRIP request with an arbitrary message code and JSON payload. Requires login. Use for commands not covered by the dedicated tools.")]
    public async Task<string> Command(
        [Description("DVRIP message code (e.g. 1020, 1042, 1452)")] int code,
        [Description("JSON payload object (SessionID is added automatically)")] JsonElement payload,
        CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>();
        if (payload.ValueKind == JsonValueKind.Object)
            foreach (var prop in payload.EnumerateObject())
                dict[prop.Name] = JsonToObject(prop.Value);
        var (raw, json, _) = await Require().CommandAsync(code, dict, ct);
        return Json(json is not null ? (object)json : new { rawBase64 = Convert.ToBase64String(raw), bytes = raw.Length });
    }

    [McpServerTool(Name = "dvr_reboot")]
    [Description("Reboot the DVR (OPMachine). Destructive: interrupts recording. Requires login and explicit confirm=true.")]
    public async Task<string> Reboot([Description("Must be true to actually reboot")] bool confirm, CancellationToken ct = default)
    {
        if (!confirm) throw new McpException("Refusing to reboot: pass confirm=true.");
        return Json(await Require().RebootAsync(ct) ?? default);
    }

    [McpServerTool(Name = "dvr_shell")]
    [Description("Run one or more shell commands on the DVR BusyBox root shell over Telnet (TCP/23). For owner-authorized administration/recovery on the local LAN. Uses the vendor root credential by default (override with user/password).")]
    public async Task<string> Shell(
        [Description("Shell commands to run in order")] string[] commands,
        [Description("DVR IP")] string? host = null,
        [Description("Telnet port")] int? port = null,
        [Description("Telnet user")] string? user = null,
        [Description("Telnet password")] string? password = null,
        CancellationToken ct = default)
    {
        var t = new TelnetClient(host ?? state.Host, port ?? state.TelnetPort, user ?? state.TelnetUser, password ?? state.TelnetPass);
        try
        {
            await t.ConnectAsync(ct);
            return Json(new { output = await t.ExecAllAsync(commands, ct) });
        }
        finally { t.Close(); }
    }

    [McpServerTool(Name = "dvr_reset_password")]
    [Description("Recover access by resetting a DVR account password via the root shell. Locates the account config, backs it up, sets the account (default admin) to the given password using the Sofia hash, and restarts the app. Owner-authorized recovery only; requires confirm=true.")]
    public async Task<string> ResetPassword(
        [Description("New password to set")] string newPassword,
        [Description("Must be true to apply the change")] bool confirm,
        [Description("Account name to reset (default admin)")] string? account = null,
        CancellationToken ct = default)
    {
        if (!confirm) throw new McpException("Refusing to reset: pass confirm=true.");
        var t = new TelnetClient(state.Host, state.TelnetPort, state.TelnetUser, state.TelnetPass);
        try
        {
            await t.ConnectAsync(ct);
            return Json(await Recovery.ResetAccountPasswordAsync(t, account ?? "admin", newPassword, ct));
        }
        finally { t.Close(); }
    }

    private static object? JsonToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(JsonToObject).ToArray(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        _ => e.ToString(),
    };
}
