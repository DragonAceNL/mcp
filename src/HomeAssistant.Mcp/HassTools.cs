using System.ComponentModel;
using System.Text.Json;
using Mcp.Common.Net;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HomeAssistant.Mcp;

[McpServerToolType]
public sealed class HassTools(HassClient client)
{
    [McpServerTool(Name = "ha_check")]
    [Description("Verify connectivity and auth to Home Assistant, and report the instance version and location.")]
    public async Task<string> Check(CancellationToken ct)
    {
        await client.PingAsync(ct);
        var cfg = await client.GetConfigAsync(ct);
        return string.Join('\n',
            $"Connected: {client.BaseUrl}",
            $"Version:   {Str(cfg, "version")}",
            $"Name:      {Str(cfg, "location_name")}",
            $"State:     {Str(cfg, "state")}");
    }

    [McpServerTool(Name = "ha_list_entities")]
    [Description("List entities with their current state. Optionally filter by domain (e.g. \"light\", \"switch\", \"sensor\") and/or a search term matched against entity_id and friendly name.")]
    public async Task<string> ListEntities(
        [Description("Entity domain prefix, e.g. \"light\", \"switch\", \"sensor\", \"binary_sensor\".")] string? domain = null,
        [Description("Case-insensitive substring matched against entity_id and friendly name.")] string? search = null,
        CancellationToken ct = default)
    {
        var d = domain?.ToLowerInvariant();
        var s = search?.ToLowerInvariant();
        var states = await client.GetStatesAsync(ct);
        var rows = new List<string>();
        foreach (var e in states.EnumerateArray())
        {
            var id = Str(e, "entity_id");
            if (d is not null && !id.StartsWith(d + ".", StringComparison.Ordinal)) continue;
            var fn = Friendly(e);
            if (s is not null && !id.ToLowerInvariant().Contains(s) && !fn.ToLowerInvariant().Contains(s)) continue;
            rows.Add($"{id} = {Str(e, "state")}{(fn.Length > 0 ? $"  ({fn})" : "")}");
        }
        rows.Sort(StringComparer.Ordinal);
        return rows.Count > 0 ? string.Join('\n', rows) : "No matching entities.";
    }

    [McpServerTool(Name = "ha_get_state")]
    [Description("Get the full state and attributes of a single entity as JSON.")]
    public async Task<string> GetState(
        [Description("e.g. \"light.office\" or \"sensor.living_room_temperature\"")] string entity_id,
        CancellationToken ct = default)
    {
        var e = await client.GetStateAsync(entity_id, ct);
        return JsonSerializer.Serialize(e, Indented);
    }

    [McpServerTool(Name = "ha_call_service")]
    [Description("Call any Home Assistant service. domain+service like \"light\"/\"turn_on\", \"switch\"/\"toggle\", \"scene\"/\"turn_on\". Provide entity_id to target, and any extra service data.")]
    public async Task<string> CallService(
        [Description("Service domain, e.g. \"light\", \"switch\", \"cover\", \"scene\".")] string domain,
        [Description("Service name, e.g. \"turn_on\", \"turn_off\", \"toggle\".")] string service,
        [Description("Target entity (or comma-separated list). Optional for services that do not need a target.")] string? entity_id = null,
        [Description("Extra service data merged into the call, e.g. {\"brightness_pct\":40,\"color_temp_kelvin\":2700}.")] JsonElement? data = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>();
        if (data is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject())
                payload[p.Name] = JsonToObject(p.Value);
        if (entity_id is not null)
            payload["entity_id"] = entity_id.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        var res = await client.CallServiceAsync(domain, service, payload, ct);
        return $"Called {domain}.{service}. Result:\n{Pretty(res)}";
    }

    [McpServerTool(Name = "ha_light")]
    [Description("Convenience for lights: turn a light on/off/toggle, with optional brightness percent, color temperature (Kelvin), or RGB colour.")]
    public async Task<string> Light(
        [Description("Light entity, e.g. \"light.office\".")] string entity_id,
        [Description("on | off | toggle (default toggle).")] string? action = null,
        [Description("Brightness 0-100 (only with action on).")] int? brightness_pct = null,
        [Description("White colour temperature in Kelvin, e.g. 2700 (warm) to 6500 (cool).")] int? color_temp_kelvin = null,
        [Description("RGB colour as [r,g,b], each 0-255.")] int[]? rgb = null,
        CancellationToken ct = default)
    {
        var act = (action ?? "toggle").ToLowerInvariant();
        if (act is not ("on" or "off" or "toggle"))
            throw new McpException("action must be on, off, or toggle");
        var service = act == "on" ? "turn_on" : act == "off" ? "turn_off" : "toggle";
        var data = new Dictionary<string, object?> { ["entity_id"] = entity_id };
        if (service == "turn_on")
        {
            if (brightness_pct is not null) data["brightness_pct"] = Math.Clamp(brightness_pct.Value, 0, 100);
            if (color_temp_kelvin is not null) data["color_temp_kelvin"] = color_temp_kelvin.Value;
            if (rgb is { Length: 3 }) data["rgb_color"] = rgb.Select(n => Math.Clamp(n, 0, 255)).ToArray();
        }
        var res = await client.CallServiceAsync("light", service, data, ct);
        return $"light.{service} on {entity_id}. Result:\n{Pretty(res)}";
    }

    [McpServerTool(Name = "ha_list_services")]
    [Description("List available service domains and services. Optionally filter by domain.")]
    public async Task<string> ListServices(
        [Description("Only show services in this domain, e.g. \"light\".")] string? domain = null,
        CancellationToken ct = default)
    {
        var d = domain?.ToLowerInvariant();
        var services = await client.GetServicesAsync(ct);
        var lines = new List<string>();
        foreach (var s in services.EnumerateArray())
        {
            var dom = Str(s, "domain");
            if (d is not null && dom != d) continue;
            var names = s.TryGetProperty("services", out var svc) && svc.ValueKind == JsonValueKind.Object
                ? string.Join(", ", svc.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal))
                : "";
            lines.Add($"{dom}: {names}");
        }
        return lines.Count > 0 ? string.Join('\n', lines) : "No matching services.";
    }

    [McpServerTool(Name = "ha_template")]
    [Description("Render a Home Assistant Jinja2 template and return the text result. Useful for areas/devices, e.g. \"{{ areas() }}\" or \"{{ area_entities('Office') }}\".")]
    public async Task<string> Template(
        [Description("Jinja2 template, e.g. \"{{ states('light.office') }}\".")] string template,
        CancellationToken ct = default)
        => await client.RenderTemplateAsync(template, ct);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "?";

    private static string Friendly(JsonElement e)
        => e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("friendly_name", out var fn)
            ? fn.GetString() ?? "" : "";

    private static string Pretty(string body)
    {
        var el = JsonHttpClient.TryParseJson(body);
        return el is null ? body : JsonSerializer.Serialize(el.Value, Indented);
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
