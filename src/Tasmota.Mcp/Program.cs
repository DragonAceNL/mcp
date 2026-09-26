using System.Text.Json;
using Mcp.Common.Config;
using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tasmota.Mcp;

var (devices, opts) = LoadConfig();
if (devices.Count == 0 && opts.ScanTargets.Count == 0)
    EnvConfig.Fail("set at least one of TASMOTA_DEVICES, TASMOTA_HOST, or TASMOTA_SCAN");

var builder = McpHost.Create(args);
builder.Services.AddSingleton(_ => new TasmotaManager(devices, opts));
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "tasmota-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<TasmotaTools>();

await builder.Build().RunAsync();
return;

static (List<TasmotaDeviceConfig> Devices, TasmotaManagerOptions Opts) LoadConfig()
{
    string? defaultPassword = EnvConfig.Get("TASMOTA_PASSWORD");
    var pwFile = EnvConfig.Get("TASMOTA_PASSWORD_FILE");
    if (pwFile is not null)
    {
        try { defaultPassword = EnvConfig.ReadSecretFile(pwFile); }
        catch (Exception e)
        {
            EnvConfig.Warn($"WARNING: cannot read TASMOTA_PASSWORD_FILE ({pwFile}): {e.Message}. Continuing without a shared password.");
        }
    }

    var scanTargets = (EnvConfig.Get("TASMOTA_SCAN") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    var opts = new TasmotaManagerOptions
    {
        DefaultUser = EnvConfig.Get("TASMOTA_USER"),
        DefaultPassword = defaultPassword,
        ScanTargets = scanTargets,
        ScanConcurrency = EnvConfig.GetInt("TASMOTA_SCAN_CONCURRENCY", 128),
        ScanTimeoutMs = EnvConfig.GetInt("TASMOTA_SCAN_TIMEOUT_MS", 800),
        CacheFile = EnvConfig.Get("TASMOTA_CACHE_FILE")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tasmota-mcp-cache.json"),
    };

    var devices = new List<TasmotaDeviceConfig>();
    var raw = EnvConfig.Get("TASMOTA_DEVICES");
    if (raw is not null)
    {
        JsonElement arr;
        try { arr = JsonDocument.Parse(raw).RootElement; }
        catch { EnvConfig.Fail("TASMOTA_DEVICES is not valid JSON"); return (devices, opts); }
        if (arr.ValueKind != JsonValueKind.Array) { EnvConfig.Fail("TASMOTA_DEVICES must be a JSON array"); return (devices, opts); }
        var i = 0;
        foreach (var d in arr.EnumerateArray())
        {
            if (!d.TryGetProperty("host", out var h) || h.ValueKind != JsonValueKind.String)
            { EnvConfig.Fail($"device[{i}] is missing \"host\""); return (devices, opts); }
            var host = h.GetString()!;
            var name = d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : host;
            string? pwd = d.TryGetProperty("passwordFile", out var pf) && pf.ValueKind == JsonValueKind.String
                ? EnvConfig.ReadSecretFile(pf.GetString()!)
                : d.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            string? user = d.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            devices.Add(new TasmotaDeviceConfig(name, host, pwd, user));
            i++;
        }
        return (devices, opts);
    }

    var singleHost = EnvConfig.Get("TASMOTA_HOST");
    if (singleHost is not null)
        devices.Add(new TasmotaDeviceConfig(EnvConfig.GetOr("TASMOTA_NAME", singleHost), singleHost, null, EnvConfig.Get("TASMOTA_USER")));
    return (devices, opts);
}
