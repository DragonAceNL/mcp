using System.Text.Json;

namespace Synology.Mcp;

/// <summary>
/// Minimal Synology DSM 7 WebAPI client (entry.cgi). Auth via SYNO.API.Auth v7
/// returns a session id (sid) passed as <c>_sid</c> on subsequent calls.
/// </summary>
public sealed class DsmClient
{
    private static readonly HttpClient Http = new();
    private readonly string _baseUrl;
    private readonly string _user;
    private readonly string _password;
    private readonly TimeSpan _timeout;
    private string? _sid;

    public DsmClient(string baseUrl, string user, string password, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _user = user;
        _password = password;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public bool LoggedIn => _sid is not null;

    private static readonly Dictionary<int, string> AuthErrors = new()
    {
        [400] = "No such account or incorrect password",
        [401] = "Account disabled",
        [402] = "Permission denied",
        [403] = "2-factor authentication code required (pass otp)",
        [404] = "2-factor authentication code invalid",
        [406] = "Enforce 2FA but not configured",
        [407] = "IP blocked (too many failures)",
        [408] = "Password expired / must change",
        [409] = "Password expired",
        [410] = "Password must change",
    };

    private async Task<JsonElement> CallAsync(string api, string method, int version,
        IReadOnlyDictionary<string, string>? parameters = null, string path = "entry.cgi", CancellationToken ct = default)
    {
        var query = new List<string>
        {
            "api=" + Uri.EscapeDataString(api),
            "method=" + Uri.EscapeDataString(method),
            "version=" + version,
        };
        if (parameters is not null)
            foreach (var (k, v) in parameters) query.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        if (_sid is not null) query.Add("_sid=" + Uri.EscapeDataString(_sid));

        var url = $"{_baseUrl}/webapi/{path}?{string.Join("&", query)}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var res = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    public async Task<(bool Ok, string? Error)> LoginAsync(string? otp, CancellationToken ct = default)
    {
        var p = new Dictionary<string, string>
        {
            ["account"] = _user,
            ["passwd"] = _password,
            ["session"] = "MCP",
            ["format"] = "sid",
        };
        if (!string.IsNullOrEmpty(otp)) p["otp_code"] = otp;
        var json = await CallAsync("SYNO.API.Auth", "login", 7, p, ct: ct).ConfigureAwait(false);
        if (json.TryGetProperty("success", out var s) && s.GetBoolean() &&
            json.TryGetProperty("data", out var data) && data.TryGetProperty("sid", out var sid))
        {
            _sid = sid.GetString();
            return (true, null);
        }
        var code = json.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        return (false, AuthErrors.TryGetValue(code, out var msg) ? msg : $"login failed (code {code})");
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (_sid is null) return;
        await CallAsync("SYNO.API.Auth", "logout", 7, new Dictionary<string, string> { ["session"] = "MCP" }, ct: ct).ConfigureAwait(false);
        _sid = null;
    }

    public async Task<JsonElement> ApiInfoAsync(string query = "all", CancellationToken ct = default)
    {
        var json = await CallAsync("SYNO.API.Info", "query", 1, new Dictionary<string, string> { ["query"] = query }, "query.cgi", ct).ConfigureAwait(false);
        return Data(json);
    }

    public async Task<JsonElement> SystemInfoAsync(CancellationToken ct = default)
        => Data(await CallAsync("SYNO.Core.System", "info", 1, ct: ct).ConfigureAwait(false));

    public async Task<JsonElement> SsInfoAsync(CancellationToken ct = default)
        => Data(await CallAsync("SYNO.SurveillanceStation.Info", "GetInfo", 1, ct: ct).ConfigureAwait(false));

    public async Task<JsonElement> SsListCamerasAsync(CancellationToken ct = default)
    {
        var json = await CallAsync("SYNO.SurveillanceStation.Camera", "List", 9, new Dictionary<string, string>
        {
            ["blFromCamList"] = "true",
            ["privCamType"] = "3",
            ["basic"] = "true",
            ["streamInfo"] = "true",
        }, ct: ct).ConfigureAwait(false);
        // If the pinned version is unsupported, surface a helpful message.
        if (json.TryGetProperty("success", out var s) && !s.GetBoolean() &&
            json.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c) && c.GetInt32() == 103)
            throw new InvalidOperationException("SYNO.SurveillanceStation.Camera.List v9 not supported on this DSM. Use syno_api_info to discover the supported version range.");
        return json;
    }

    public Task<JsonElement> RequestAsync(string api, string method, int version, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
        => CallAsync(api, method, version, parameters, ct: ct);

    private static JsonElement Data(JsonElement json)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var d) ? d : json;
}
