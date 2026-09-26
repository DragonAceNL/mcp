using System.ComponentModel;
using System.Text.Json;
using Mcp.Common.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Tasmota.Mcp;

[McpServerToolType]
public sealed class TasmotaTools(TasmotaManager manager)
{
    [McpServerTool(Name = "tasmota_list_devices")]
    [Description("List the configured Tasmota devices (names and hosts).")]
    public string ListDevices()
    {
        var list = manager.List();
        return list.Count > 0
            ? string.Join('\n', list.Select(d => $"{d.Name} — {d.Host} [{d.Source}]"))
            : "No devices yet — run tasmota_discover, or address a device by IP directly.";
    }

    [McpServerTool(Name = "tasmota_status")]
    [Description("Get a readable status summary (name, IP/hostname, module, firmware, Wi-Fi, relay power states, uptime).")]
    public async Task<string> Status(
        [Description("Which Tasmota device (name or IP). Optional if only one is configured.")] string? device = null,
        CancellationToken ct = default)
    {
        var dev = manager.Get(device);
        var res = await dev.CommandAsync("Status 0", ct: ct);
        var s = AsElement(res);
        var st = Obj(s, "Status");
        var net = Obj(s, "StatusNET");
        var fwr = Obj(s, "StatusFWR");
        var sts = Obj(s, "StatusSTS");
        var wifi = Obj(sts, "Wifi");
        var powers = sts is { ValueKind: JsonValueKind.Object }
            ? string.Join(' ', sts.Value.EnumerateObject().Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Name, "^POWER\\d*$")).Select(p => $"{p.Name}={p.Value}"))
            : "";
        return string.Join('\n',
            $"Device:    {Str(st, "DeviceName")} ({dev.Name})",
            $"Host/IP:   {Str(net, "Hostname")}  /  {Str(net, "IPAddress")}",
            $"MAC:       {Str(net, "Mac")}",
            $"Module/FW: module {Str(st, "Module")}, Tasmota {Str(fwr, "Version")} ({Str(fwr, "Hardware")})",
            $"Wi-Fi:     SSID {Str(wifi, "SSId")}, RSSI {Str(wifi, "Signal")} dBm (ch {Str(wifi, "Channel")})",
            $"Relays:    {(powers.Length > 0 ? powers : "(none)")}",
            $"Uptime:    {Str(sts, "Uptime")}");
    }

    [McpServerTool(Name = "tasmota_command")]
    [Description("Run any raw Tasmota command via the HTTP API (e.g. \"Status 0\", \"Power2 TOGGLE\", \"Hostname garden-switch\", \"WebPassword <pw>\"). Returns the JSON result.")]
    public async Task<string> Command(
        [Description("Tasmota command, e.g. \"Power1 ON\" or \"Status 0\"")] string command,
        [Description("Which Tasmota device (name or IP). Optional if only one is configured.")] string? device = null,
        CancellationToken ct = default)
    {
        var res = await manager.Get(device).CommandAsync(command, ct: ct);
        return res is string s ? s : JsonSerializer.Serialize((JsonElement)res, Indented);
    }

    [McpServerTool(Name = "tasmota_power")]
    [Description("Switch a relay on/off/toggle. output 0 = all relays, 1-8 = a specific relay.")]
    public async Task<string> Power(
        [Description("Relay index: 0 = all, 1-8 = specific (default 1)")] int output = 1,
        [Description("ON | OFF | TOGGLE (default TOGGLE)")] string? state = null,
        [Description("Which Tasmota device (name or IP). Optional if only one is configured.")] string? device = null,
        CancellationToken ct = default)
    {
        var outp = Math.Clamp(output, 0, 8);
        var stateRaw = (state ?? "TOGGLE").ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(stateRaw, "^(ON|OFF|TOGGLE|1|0|2)$"))
            throw new McpException("state must be ON, OFF, or TOGGLE");
        var res = await manager.Get(device).CommandAsync($"Power{outp} {stateRaw}", ct: ct);
        return res is string s ? s : JsonSerializer.Serialize((JsonElement)res);
    }

    [McpServerTool(Name = "tasmota_set_name")]
    [Description("Set friendly names: hostname (what the router shows), device name, and/or friendly name 1. Changing the hostname reconnects Wi-Fi briefly.")]
    public async Task<string> SetName(
        [Description("Network hostname (letters/digits/hyphen, e.g. garden-switch)")] string? hostname = null,
        [Description("Device name shown in the UI / Home Assistant")] string? deviceName = null,
        [Description("Friendly name for output 1")] string? friendlyName = null,
        [Description("Which Tasmota device (name or IP). Optional if only one is configured.")] string? device = null,
        CancellationToken ct = default)
    {
        var dev = manager.Get(device);
        var results = new List<string>();
        if (hostname is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(hostname, "^[A-Za-z0-9-]{1,32}$"))
                throw new McpException("hostname must be 1-32 chars of letters, digits, or hyphen");
            results.Add($"Hostname -> {ToJson(await dev.CommandAsync($"Hostname {hostname}", ct: ct))}");
        }
        if (deviceName is not null)
            results.Add($"DeviceName -> {ToJson(await dev.CommandAsync($"DeviceName {deviceName}", ct: ct))}");
        if (friendlyName is not null)
            results.Add($"FriendlyName1 -> {ToJson(await dev.CommandAsync($"FriendlyName1 {friendlyName}", ct: ct))}");
        if (results.Count == 0)
            throw new McpException("Provide at least one of: hostname, deviceName, friendlyName");
        return string.Join('\n', results);
    }

    [McpServerTool(Name = "tasmota_set_web_password")]
    [Description("Set the device's web-admin password to the value configured for this device in mcp.json (password/passwordFile). Run after adding the password to the config so the secret is never passed as a tool argument. Afterwards the device requires auth and the MCP uses the same configured password automatically.")]
    public async Task<string> SetWebPassword(
        [Description("Which Tasmota device (name or IP). Optional if only one is configured.")] string? device = null,
        CancellationToken ct = default)
    {
        var dev = manager.Get(device);
        var res = await dev.ApplyConfiguredWebPasswordAsync(ct);
        return $"Web-admin password set on {dev.Name} from local config. Result: {ToJson(res)}";
    }

    [McpServerTool(Name = "tasmota_discover")]
    [Description("Scan the configured subnets (TASMOTA_SCAN, e.g. \"192.168.4.0/24,192.168.10.0/24\") for Tasmota devices and register them by hostname so you can address them by name. Uses the shared password.")]
    public Task<string> Discover(CancellationToken ct = default) => manager.DiscoverAsync(ct);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonElement AsElement(object res)
        => res is JsonElement el ? el : default;

    private static JsonElement? Obj(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string Str(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "?" : v.ToString()
            : "?";

    private static string ToJson(object res) => res is string s ? JsonSerializer.Serialize(s) : JsonSerializer.Serialize((JsonElement)res);
}
