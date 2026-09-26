using System.Text.Json;
using Mcp.Common.Config;
using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tomato.Mcp;

var configs = LoadConfigs();
var builder = McpHost.Create(args);
builder.Services.AddSingleton(new TomatoManager(configs));
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "tomato-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<TomatoTools>();

await builder.Build().RunAsync();
return;

static List<TomatoRouterConfig> LoadConfigs()
{
    var raw = EnvConfig.Get("TOMATO_ROUTERS");
    if (raw is not null)
    {
        JsonElement arr;
        try { arr = JsonDocument.Parse(raw).RootElement; }
        catch { EnvConfig.Fail("TOMATO_ROUTERS is not valid JSON"); return new(); }
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        { EnvConfig.Fail("TOMATO_ROUTERS must be a non-empty JSON array"); return new(); }

        var list = new List<TomatoRouterConfig>();
        var i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("host", out var h) || h.ValueKind != JsonValueKind.String)
            { EnvConfig.Fail($"router[{i}] is missing \"host\""); return new(); }
            var host = h.GetString()!;
            var name = Prop(e, "name") ?? host;
            var keyPath = Prop(e, "privateKeyPath");
            var privateKey = keyPath is not null ? EnvConfig.ReadSecretFile(keyPath) : Prop(e, "privateKey");
            var password = Prop(e, "password");
            if (password is null && privateKey is null && keyPath is null)
            { EnvConfig.Fail($"router \"{name}\" needs a password, privateKey, or privateKeyPath"); return new(); }
            var port = e.TryGetProperty("port", out var p) && p.ValueKind is JsonValueKind.Number ? p.GetInt32()
                     : e.TryGetProperty("port", out var ps) && ps.ValueKind == JsonValueKind.String && int.TryParse(ps.GetString(), out var pn) ? pn : 22;
            list.Add(new TomatoRouterConfig(name, host, port, Prop(e, "username") ?? "root", password, keyPath is not null ? null : privateKey, keyPath));
            i++;
        }
        return list;
    }

    var singleHost = EnvConfig.Get("TOMATO_HOST");
    if (singleHost is null)
    { EnvConfig.Fail("set TOMATO_ROUTERS (JSON) or TOMATO_HOST for a single router"); return new(); }
    var kp = EnvConfig.Get("TOMATO_PRIVATE_KEY_PATH");
    var pk = kp is not null ? null : EnvConfig.Get("TOMATO_PRIVATE_KEY");
    var pw = EnvConfig.Get("TOMATO_PASSWORD");
    if (pw is null && pk is null && kp is null)
        EnvConfig.Fail("set TOMATO_PASSWORD, TOMATO_PRIVATE_KEY, or TOMATO_PRIVATE_KEY_PATH");
    return new List<TomatoRouterConfig>
    {
        new(EnvConfig.GetOr("TOMATO_NAME", singleHost), singleHost, EnvConfig.GetInt("TOMATO_PORT", 22),
            EnvConfig.GetOr("TOMATO_USER", "root"), pw, pk, kp),
    };
}

static string? Prop(JsonElement e, string name)
    => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;
