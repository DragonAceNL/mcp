using System.Net;
using System.Text;
using System.Text.Json;

namespace Mcp.Common.Net;

/// <summary>
/// Small typed HTTP helper shared by the HTTP-based servers (Synology, Home
/// Assistant, Tasmota). Wraps a single <see cref="HttpClient"/> with a base URL
/// and a per-request timeout, and exposes both JSON and raw-text access because
/// some device APIs return plain text (e.g. HA <c>/api/template</c>).
/// </summary>
public sealed class JsonHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public JsonHttpClient(string? baseUrl = null, TimeSpan? timeout = null, IEnumerable<KeyValuePair<string, string>>? defaultHeaders = null)
    {
        _http = new HttpClient();
        // Per-request timeouts are enforced via CancellationTokens below, so disable
        // HttpClient's own 100s cap (it would otherwise override longer overrides).
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(baseUrl))
            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (defaultHeaders is not null)
            foreach (var h in defaultHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private CancellationTokenSource NewCts(CancellationToken outer, TimeSpan? timeout = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout ?? _timeout);
        return cts;
    }

    /// <summary>Send a request and return status + raw body text.</summary>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage req, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var effective = timeout ?? _timeout;
        using var cts = NewCts(ct, effective);
        try
        {
            using var res = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (res.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout after {effective.TotalMilliseconds:0}ms calling {req.RequestUri}");
        }
    }

    public Task<(HttpStatusCode, string)> GetAsync(string relativeOrAbsolute, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, relativeOrAbsolute), ct);

    public Task<(HttpStatusCode, string)> PostJsonAsync(string relativeOrAbsolute, object body, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, relativeOrAbsolute)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return SendAsync(req, ct);
    }

    /// <summary>Parse a body as a JsonElement, or throw a clear error.</summary>
    public static JsonElement ParseJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>Try to parse a body as JSON; return null if it is not JSON (plain text).</summary>
    public static JsonElement? TryParseJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
