using System.Net;
using System.Text.Json;

namespace Tasmota.Mcp;

public sealed record TasmotaDeviceConfig(string Name, string Host, string? Password = null, string? User = null);

/// <summary>
/// HTTP client for one Tasmota device's command API
/// (<c>http://&lt;host&gt;/cm?cmnd=&lt;command&gt;</c>). Pure query API — no shell surface.
/// </summary>
public sealed class TasmotaClient
{
    private static readonly HttpClient Http = new();
    private readonly TasmotaDeviceConfig _cfg;

    public TasmotaClient(TasmotaDeviceConfig cfg) => _cfg = cfg;

    public string Name => _cfg.Name;
    public string Host => _cfg.Host;

    private static string BaseUrl(string host)
        => host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? host.TrimEnd('/')
            : "http://" + host;

    /// <summary>Run one Tasmota command; returns parsed JSON element or raw text.</summary>
    public async Task<object> CommandAsync(string cmnd, int timeoutMs = 10000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmnd)) throw new ArgumentException("Empty Tasmota command");
        if (cmnd.Length > 512) throw new ArgumentException("Tasmota command too long");

        var query = new List<string>();
        if (_cfg.Password is not null)
        {
            query.Add("user=" + Uri.EscapeDataString(_cfg.User ?? "admin"));
            query.Add("password=" + Uri.EscapeDataString(_cfg.Password));
        }
        query.Add("cmnd=" + Uri.EscapeDataString(cmnd));
        var url = $"{BaseUrl(_cfg.Host)}/cm?{string.Join("&", query)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var res = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)res.StatusCode} from {_cfg.Name}: {Trunc(text, 200)}");
            var el = TryParse(text);
            return el is null ? text : (object)el.Value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout talking to {_cfg.Name} ({_cfg.Host})");
        }
    }

    /// <summary>Set the device web-admin password to the one configured for this device.</summary>
    public Task<object> ApplyConfiguredWebPasswordAsync(CancellationToken ct = default)
    {
        if (_cfg.Password is null)
            throw new InvalidOperationException(
                "No password configured for this device. Add \"password\" or \"passwordFile\" to its config entry and restart the server first.");
        return CommandAsync($"WebPassword {_cfg.Password}", ct: ct);
    }

    internal static JsonElement? TryParse(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Probe a host: is it a Tasmota? Returns its hostname if so.</summary>
    public static async Task<(string Ip, string Hostname)?> ProbeAsync(string host, string? user, string? password, int timeoutMs)
    {
        var query = new List<string>();
        if (password is not null)
        {
            query.Add("user=" + Uri.EscapeDataString(user ?? "admin"));
            query.Add("password=" + Uri.EscapeDataString(password));
        }
        query.Add("cmnd=Status%205"); // StatusNET (has Hostname)
        var url = $"{BaseUrl(host)}/cm?{string.Join("&", query)}";
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var res = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var el = TryParse(text);
            if (el is { ValueKind: JsonValueKind.Object } root &&
                root.TryGetProperty("StatusNET", out var net) && net.ValueKind == JsonValueKind.Object &&
                net.TryGetProperty("Hostname", out var hn) && hn.ValueKind == JsonValueKind.String)
                return (host, hn.GetString() ?? host);
            return null;
        }
        catch { return null; }
    }
}
