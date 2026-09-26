using System.Net;
using System.Text.Json;
using Mcp.Common.Config;
using Mcp.Common.Net;

namespace HomeAssistant.Mcp;

/// <summary>Thin client over the Home Assistant REST API (<c>/api</c>).</summary>
public sealed class HassClient
{
    private readonly JsonHttpClient _http;
    public string BaseUrl { get; }

    public HassClient(string baseUrl, string token, TimeSpan? timeout)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new JsonHttpClient(
            baseUrl: BaseUrl,
            timeout: timeout,
            defaultHeaders: new[] { new KeyValuePair<string, string>("Authorization", $"Bearer {token}") });
    }

    public static HassClient FromEnv()
    {
        var baseUrl = EnvConfig.GetOr("HASS_URL", "http://homeassistant.local:8123");
        var token = LoadToken();
        var timeoutMs = EnvConfig.GetInt("HASS_TIMEOUT_MS", 10000);
        return new HassClient(baseUrl, token, TimeSpan.FromMilliseconds(timeoutMs));
    }

    private static string LoadToken()
    {
        var file = EnvConfig.Get("HASS_TOKEN_FILE");
        if (file is not null) return EnvConfig.ReadSecretFile(file);
        var inline = EnvConfig.Get("HASS_TOKEN");
        if (!string.IsNullOrWhiteSpace(inline)) return inline.Trim();
        EnvConfig.Fail("no token. Set HASS_TOKEN_FILE (path to a file containing a Home Assistant Long-Lived Access Token) or HASS_TOKEN.");
        return string.Empty; // unreachable
    }

    private async Task<string> RequestAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(method, "api" + path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var (status, text) = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            var hint = status == HttpStatusCode.Unauthorized ? " (401 Unauthorized — check the Long-Lived Access Token)" : "";
            throw new InvalidOperationException($"HTTP {(int)status} {status}{hint}: {Trunc(text, 300)}");
        }
        return text;
    }

    private static bool IsSuccess(HttpStatusCode s) => (int)s is >= 200 and < 300;
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    public Task<string> PingAsync(CancellationToken ct = default) => RequestAsync(HttpMethod.Get, "/", null, ct);

    public async Task<JsonElement> GetConfigAsync(CancellationToken ct = default)
        => JsonHttpClient.ParseJson(await RequestAsync(HttpMethod.Get, "/config", null, ct));

    public async Task<JsonElement> GetStatesAsync(CancellationToken ct = default)
        => JsonHttpClient.ParseJson(await RequestAsync(HttpMethod.Get, "/states", null, ct));

    public async Task<JsonElement> GetStateAsync(string entityId, CancellationToken ct = default)
        => JsonHttpClient.ParseJson(await RequestAsync(HttpMethod.Get, "/states/" + Uri.EscapeDataString(entityId), null, ct));

    public async Task<JsonElement> GetServicesAsync(CancellationToken ct = default)
        => JsonHttpClient.ParseJson(await RequestAsync(HttpMethod.Get, "/services", null, ct));

    public async Task<string> CallServiceAsync(string domain, string service, object data, CancellationToken ct = default)
        => await RequestAsync(HttpMethod.Post, $"/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}", data, ct);

    public Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/template", new { template }, ct);
}
