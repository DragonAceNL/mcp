using System.Text.Json;
using Mcp.Common.Config;
using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Windows.Mcp;

var configs = LoadConfigs();
var builder = McpHost.Create(args);
builder.Services.AddSingleton(new WindowsManager(configs));
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "windows-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<WindowsTools>();

await builder.Build().RunAsync();
return;

// Config sources (mirrors the other servers): WINDOWS_SERVERS as a JSON array for
// multiple hosts, or WINDOWS_HOST + WINDOWS_* for a single host.
static List<WindowsServerConfig> LoadConfigs()
{
    var raw = EnvConfig.Get("WINDOWS_SERVERS");
    if (raw is not null)
    {
        JsonElement arr;
        try { arr = JsonDocument.Parse(raw).RootElement; }
        catch { EnvConfig.Fail("WINDOWS_SERVERS is not valid JSON"); return new(); }
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        { EnvConfig.Fail("WINDOWS_SERVERS must be a non-empty JSON array"); return new(); }

        var list = new List<WindowsServerConfig>();
        var i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("host", out var h) || h.ValueKind != JsonValueKind.String)
            { EnvConfig.Fail($"server[{i}] is missing \"host\""); return new(); }
            var host = h.GetString()!;
            var name = Prop(e, "name") ?? host;
            var useSsl = Bool(e, "useSsl");
            var username = Prop(e, "username");
            var pwFile = Prop(e, "passwordFile");
            var password = pwFile is not null ? EnvConfig.ReadSecretFile(pwFile) : Prop(e, "password");
            var auth = Prop(e, "auth") ?? "Negotiate";
            var configName = Prop(e, "configurationName");
            var skipCert = Bool(e, "skipCertCheck");
            var port = Port(e, useSsl);
            list.Add(new WindowsServerConfig(name, host, port, useSsl, username, password, auth, configName, skipCert));
            i++;
        }
        return list;
    }

    var singleHost = EnvConfig.Get("WINDOWS_HOST");
    if (singleHost is null)
    { EnvConfig.Fail("set WINDOWS_SERVERS (JSON) or WINDOWS_HOST for a single server"); return new(); }
    var ssl = EnvConfig.Get("WINDOWS_USE_SSL") is "1" or "true" or "True";
    var pwf = EnvConfig.Get("WINDOWS_PASSWORD_FILE");
    var pw = pwf is not null ? EnvConfig.ReadSecretFile(pwf) : EnvConfig.Get("WINDOWS_PASSWORD");
    return new List<WindowsServerConfig>
    {
        new(
            Name: EnvConfig.GetOr("WINDOWS_NAME", singleHost),
            Host: singleHost,
            Port: EnvConfig.GetInt("WINDOWS_PORT", ssl ? 5986 : 5985),
            UseSsl: ssl,
            Username: EnvConfig.Get("WINDOWS_USER"),
            Password: pw,
            Auth: EnvConfig.GetOr("WINDOWS_AUTH", "Negotiate"),
            ConfigurationName: EnvConfig.Get("WINDOWS_CONFIGURATION_NAME"),
            SkipCertCheck: EnvConfig.Get("WINDOWS_SKIP_CERT_CHECK") is "1" or "true" or "True"),
    };

    static string? Prop(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && v.GetString() is "1" or "true" or "True"));

    static int Port(JsonElement e, bool useSsl)
    {
        if (e.TryGetProperty("port", out var p))
        {
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var n)) return n;
        }
        return useSsl ? 5986 : 5985;
    }
}
