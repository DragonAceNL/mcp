using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Synology.Mcp;

[McpServerToolType]
public sealed class SynologyTools(SynologyState state)
{
    [McpServerTool(Name = "syno_login")]
    [Description("Authenticate to Synology DSM. Reads the password from SYNO_PASSWORD_FILE (recommended) so it never passes through chat. Supports optional 2FA one-time code.")]
    public async Task<string> Login(
        [Description("2FA one-time code, if 2-step verification is enabled")] string? otp = null,
        CancellationToken ct = default)
    {
        if (!state.HasPassword)
            throw new McpException("SYNO_PASSWORD_FILE (or SYNO_PASSWORD) is not set.");
        var client = state.NewClient();
        var (ok, error) = await client.LoginAsync(otp, ct);
        if (!ok)
        {
            state.Client = null;
            throw new McpException(error ?? "login failed");
        }
        state.Client = client;
        return Json(new { ok = true, user = state.User, baseUrl = state.BaseUrl });
    }

    [McpServerTool(Name = "syno_logout")]
    [Description("End the current DSM session.")]
    public async Task<string> Logout(CancellationToken ct = default)
    {
        if (state.Client is not null) await state.Client.LogoutAsync(ct);
        state.Client = null;
        return Json(new { ok = true });
    }

    [McpServerTool(Name = "syno_api_info")]
    [Description("List DSM APIs available (name, version range, path). Useful to see if Surveillance Station is installed.")]
    public async Task<string> ApiInfo(
        [Description("Comma-separated API names, or \"all\" (default)")] string? query = null,
        CancellationToken ct = default)
    {
        var client = state.Client ?? state.NewClient(); // API info is unauthenticated
        return Json(await client.ApiInfoAsync(query ?? "all", ct));
    }

    [McpServerTool(Name = "syno_system_info")]
    [Description("Get DSM system information (model, serial, DSM version, uptime). Requires login.")]
    public async Task<string> SystemInfo(CancellationToken ct = default)
        => Json(await Require().SystemInfoAsync(ct));

    [McpServerTool(Name = "syno_ss_info")]
    [Description("Get Surveillance Station info (version, license, camera count). Requires login and Surveillance Station installed.")]
    public async Task<string> SsInfo(CancellationToken ct = default)
        => Json(await Require().SsInfoAsync(ct));

    [McpServerTool(Name = "syno_ss_list_cameras")]
    [Description("List cameras configured in Surveillance Station. Requires login and Surveillance Station installed.")]
    public async Task<string> SsListCameras(CancellationToken ct = default)
        => Json(await Require().SsListCamerasAsync(ct));

    [McpServerTool(Name = "syno_request")]
    [Description("Advanced escape hatch: call any DSM WebAPI (api, method, version, params). Requires login. Params are string/number key-values.")]
    public async Task<string> Request(
        [Description("API name, e.g. SYNO.Core.System")] string api,
        [Description("Method, e.g. info")] string method,
        [Description("API version, e.g. 1")] int version,
        [Description("Extra query parameters as key-value pairs")] JsonElement? parameters = null,
        CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>();
        if (parameters is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        return Json(await Require().RequestAsync(api, method, version, dict, ct));
    }

    private DsmClient Require()
        => state.Client is { LoggedIn: true } c ? c : throw new McpException("Not logged in. Call syno_login first.");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static string Json(object o) => JsonSerializer.Serialize(o, Indented);
    private static string Json(JsonElement e) => JsonSerializer.Serialize(e, Indented);
}

/// <summary>Process-global DSM session + connection parameters.</summary>
public sealed class SynologyState
{
    public required string BaseUrl { get; init; }
    public required string User { get; init; }
    public bool HasPassword { get; init; }
    public required Func<DsmClient> Factory { get; init; }
    public DsmClient? Client { get; set; }
    public DsmClient NewClient() => Factory();
}
