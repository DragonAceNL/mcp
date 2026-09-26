using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Tomato.Mcp;

[McpServerToolType]
public sealed class TomatoTools(TomatoManager manager)
{
    private const string RouterDesc = "Which router to target (name or IP). Optional if only one router is configured.";

    [McpServerTool(Name = "tomato_list_routers")]
    [Description("List the configured Tomato routers (names and IPs) this server manages")]
    public string ListRouters()
    {
        var list = manager.List();
        return list.Count > 0 ? string.Join('\n', list.Select(r => $"{r.Name} — {r.Host}")) : "No routers configured";
    }

    [McpServerTool(Name = "tomato_system_info")]
    [Description("Get router name, model, firmware, kernel, and uptime")]
    public async Task<string> SystemInfo([Description(RouterDesc)] string? router = null, CancellationToken ct = default)
    {
        var i = await manager.Get(router).GetSystemInfoAsync(ct);
        return string.Join('\n',
            $"Router:   {i.Name} ({i.Host})",
            $"Name:     {i.RouterName}",
            $"Model:    {i.Model}",
            $"Firmware: {i.Firmware}",
            $"Kernel:   {i.Kernel}",
            $"Uptime:   {i.Uptime}");
    }

    [McpServerTool(Name = "tomato_cpu_load")]
    [Description("Get CPU load average and top processes")]
    public Task<string> CpuLoad([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetCpuLoadAsync(ct);

    [McpServerTool(Name = "tomato_memory")]
    [Description("Get memory usage")]
    public Task<string> Memory([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetMemoryAsync(ct);

    [McpServerTool(Name = "tomato_nvram_get")]
    [Description("Get a single NVRAM configuration value by key")]
    public Task<string> NvramGet([Description("NVRAM key (e.g., router_name, wan_ipaddr, qos_enable)")] string key, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).NvramGetAsync(key, ct);

    [McpServerTool(Name = "tomato_nvram_show")]
    [Description("Show NVRAM settings, optionally filtered by a substring (e.g., \"wan\", \"wl\", \"qos\")")]
    public Task<string> NvramShow([Description("Filter substring (optional; omit to show all)")] string? pattern = null, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).NvramShowAsync(pattern, ct);

    [McpServerTool(Name = "tomato_interfaces")]
    [Description("List network interfaces and their addresses")]
    public Task<string> Interfaces([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetInterfacesAsync(ct);

    [McpServerTool(Name = "tomato_interface_counters")]
    [Description("Get RX/TX counters incl. errors and drops (ip -s link). Optionally one interface")]
    public Task<string> InterfaceCounters([Description("Interface name (optional; all if omitted)")] string? @interface = null, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).GetInterfaceCountersAsync(@interface, ct);

    [McpServerTool(Name = "tomato_arp")]
    [Description("Get the ARP table (connected devices)")]
    public Task<string> Arp([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetArpAsync(ct);

    [McpServerTool(Name = "tomato_routes")]
    [Description("Get the routing table")]
    public Task<string> Routes([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetRoutesAsync(ct);

    [McpServerTool(Name = "tomato_dhcp_leases")]
    [Description("Get active DHCP leases (dnsmasq)")]
    public Task<string> DhcpLeases([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetDhcpLeasesAsync(ct);

    [McpServerTool(Name = "tomato_wireless_status")]
    [Description("Get wireless radio status (SSID, channel, etc.) per interface")]
    public Task<string> WirelessStatus([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetWirelessStatusAsync(ct);

    [McpServerTool(Name = "tomato_wireless_clients")]
    [Description("List associated wireless clients with RSSI/rate/quality per radio")]
    public Task<string> WirelessClients([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetWirelessClientsAsync(ct);

    [McpServerTool(Name = "tomato_qos_status")]
    [Description("Get QoS configuration (nvram qos_*) and live tc qdisc stats")]
    public Task<string> QosStatus([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetQosStatusAsync(ct);

    [McpServerTool(Name = "tomato_qdisc_stats")]
    [Description("Get live tc qdisc stats (drops/overlimits) for one interface")]
    public Task<string> QdiscStats([Description("Interface name (e.g., br0, vlan1, eth0)")] string @interface, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).GetQdiscStatsAsync(@interface, ct);

    [McpServerTool(Name = "tomato_igmp_snooping")]
    [Description("Get IGMP snooping / multicast forwarding status (IPTV / multicast over WiFi)")]
    public Task<string> IgmpSnooping([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetIgmpSnoopingAsync(ct);

    [McpServerTool(Name = "tomato_nvram_set")]
    [Description("Set one or more nvram variables (does NOT commit unless commit=true). Keys/values are validated.")]
    public async Task<string> NvramSet(
        [Description("Object of nvram key/value pairs to set, e.g. { \"emf_enable\": \"1\" }")] JsonElement pairs,
        [Description("Persist to flash with nvram commit after setting (default: false)")] bool commit = false,
        [Description(RouterDesc)] string? router = null,
        CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>();
        if (pairs.ValueKind == JsonValueKind.Object)
            foreach (var p in pairs.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        var r = manager.Get(router);
        var setRes = await r.NvramSetAsync(dict, ct);
        var commitRes = commit ? $"\n{await r.NvramCommitAsync(ct)}" : "\n(not committed)";
        return $"{setRes}{commitRes}";
    }

    [McpServerTool(Name = "tomato_service_restart")]
    [Description("Restart/start/stop/reload a Tomato service (e.g. target \"wireless\", \"net\"). Brief disruption on that service.")]
    public Task<string> ServiceRestart(
        [Description("Service target (e.g. wireless, net, firewall, dnsmasq)")] string target,
        [Description("restart | start | stop | reload (default: restart)")] string? action = null,
        [Description(RouterDesc)] string? router = null,
        CancellationToken ct = default)
        => manager.Get(router).RestartServiceAsync(target, action ?? "restart", ct);

    [McpServerTool(Name = "tomato_enable_iptv_multicast")]
    [Description("Enable EMF + per-radio WMF + bridge IGMP snooping to stop IPTV multicast flooding over WiFi (fixes TV stutter). Sets nvram, commits, and restarts wireless (brief WiFi blip on that AP).")]
    public Task<string> EnableIptvMulticast([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).EnableIptvMulticastAsync(ct);

    [McpServerTool(Name = "tomato_set_client_isolation")]
    [Description("Enable/disable wireless client (AP) isolation on a BSS such as wl0.2 (IoT SSID) or wl0.1 (Guest SSID). Blocks same-AP client-to-client traffic while leaving gateway-routed flows (e.g. Home Assistant -> IoT) working. Applies live (no WiFi drop) and persists to nvram. Isolation is per-AP only.")]
    public Task<string> SetClientIsolation(
        [Description("Wireless BSS interface, e.g. wl0.2 (IoT) or wl0.1 (Guest)")] string @interface,
        [Description("true = enable isolation, false = disable")] bool enabled,
        [Description(RouterDesc)] string? router = null,
        CancellationToken ct = default)
        => manager.Get(router).SetClientIsolationAsync(@interface, enabled, ct);

    [McpServerTool(Name = "tomato_connections")]
    [Description("Get active connection (conntrack) count")]
    public Task<string> Connections([Description(RouterDesc)] string? router = null, CancellationToken ct = default) => manager.Get(router).GetConnectionsAsync(ct);

    [McpServerTool(Name = "tomato_logs")]
    [Description("Get recent system log entries")]
    public Task<string> Logs([Description("Number of log lines (default: 50)")] int lines = 50, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).GetSyslogAsync(lines, ct);

    [McpServerTool(Name = "tomato_ping")]
    [Description("Ping a host from the router")]
    public Task<string> Ping([Description("Host to ping (IP or hostname)")] string host, [Description("Number of pings (default: 4)")] int count = 4, [Description(RouterDesc)] string? router = null, CancellationToken ct = default)
        => manager.Get(router).PingAsync(host, count, ct);
}
