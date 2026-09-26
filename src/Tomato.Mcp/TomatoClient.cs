using System.Text.RegularExpressions;
using Mcp.Common.Ssh;
using Mcp.Common.Text;
using ModelContextProtocol;

namespace Tomato.Mcp;

public sealed record TomatoRouterConfig(string Name, string Host, int Port, string Username, string? Password, string? PrivateKey, string? PrivateKeyPath);

/// <summary>
/// SSH client for one FreshTomato router/AP (dropbear). Wraps the shared
/// serialised SSH session with the lenient Tomato exit policy and legacy
/// algorithm support. Ports every helper from the TS <c>tomato-ssh.ts</c>.
/// </summary>
public sealed class TomatoClient
{
    private readonly SshCommandSession _ssh;
    public string Name { get; }
    public string Host { get; }

    public TomatoClient(TomatoRouterConfig cfg)
    {
        Name = cfg.Name;
        Host = cfg.Host;
        _ssh = new SshCommandSession(new SshOptions
        {
            Host = cfg.Host,
            Port = cfg.Port,
            Username = cfg.Username,
            Password = cfg.Password,
            PrivateKey = cfg.PrivateKey,
            PrivateKeyPath = cfg.PrivateKeyPath,
            AllowLegacyAlgorithms = true,
            StrictExit = false,
        });
    }

    public void Disconnect() => _ssh.Disconnect();
    private Task<string> Exec(string cmd, CancellationToken ct = default) => _ssh.ExecAsync(cmd, ct);

    // ---- System -----------------------------------------------------------

    public async Task<(string Name, string Host, string RouterName, string Model, string Firmware, string Kernel, string Uptime)> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var routerName = await Safe(Exec("nvram get router_name", ct));
        var model = await Safe(Exec("nvram get t_model_name || nvram get t_model || nvram get pmonitor_model || nvram get boardtype", ct));
        var firmware = await Safe(Exec("nvram get os_version || cat /proc/version 2>/dev/null | head -c 120", ct));
        var kernel = await Safe(Exec("uname -r", ct));
        var uptime = await Safe(Exec("uptime", ct));
        return (Name, Host, Or(routerName), Or(model), Or(firmware), Or(kernel), Or(uptime));
    }

    public Task<string> GetCpuLoadAsync(CancellationToken ct = default) => Exec("cat /proc/loadavg; echo \"---\"; top -bn1 2>/dev/null | head -12 || uptime", ct);
    public Task<string> GetMemoryAsync(CancellationToken ct = default) => Exec("free 2>/dev/null || cat /proc/meminfo | head -5", ct);

    // ---- NVRAM ------------------------------------------------------------

    public Task<string> NvramGetAsync(string key, CancellationToken ct = default)
    {
        var safe = Validate.Sanitize(key);
        if (safe.Length == 0) throw new McpException("Invalid nvram key");
        return Exec($"nvram get {safe}", ct);
    }

    public Task<string> NvramShowAsync(string? pattern, CancellationToken ct = default)
    {
        if (pattern is not null)
        {
            var safe = Validate.Sanitize(pattern, "*");
            if (safe.Length == 0) throw new McpException("Invalid pattern");
            return Exec($"nvram show 2>/dev/null | grep -i \"{safe}\" | sort", ct);
        }
        return Exec("nvram show 2>/dev/null | sort", ct);
    }

    public Task<string> NvramSetAsync(IReadOnlyDictionary<string, string> pairs, CancellationToken ct = default)
    {
        var cmds = new List<string>();
        foreach (var (key, value) in pairs)
        {
            if (!Validate.IsNvramKey(key)) throw new McpException($"Invalid nvram key: {key}");
            var safeVal = value.Replace("'", "'\\''");
            cmds.Add($"nvram set {key}='{safeVal}'");
        }
        if (cmds.Count == 0) throw new McpException("No nvram pairs provided");
        cmds.Add("echo OK");
        return Exec(string.Join("; ", cmds), ct);
    }

    public Task<string> NvramCommitAsync(CancellationToken ct = default) => Exec("nvram commit && echo \"committed\"", ct);

    public Task<string> RestartServiceAsync(string target, string action, CancellationToken ct = default)
    {
        if (!Validate.IsServiceTarget(target)) throw new McpException("Invalid service target");
        if (!Validate.IsServiceAction(action)) throw new McpException("Invalid service action");
        return Exec($"service {target} {action} 2>&1; echo \"done: {target} {action}\"", ct);
    }

    // ---- IPTV multicast ---------------------------------------------------

    public async Task<string> EnableIptvMulticastAsync(CancellationToken ct = default)
    {
        var ifaceOut = await Safe(Exec("nvram get wl_ifnames", ct));
        var units = new HashSet<string>();
        for (var i = 0; i < 4; i++)
        {
            var name = await Safe(Exec($"nvram get wl{i}_ifname", ct));
            if (!string.IsNullOrWhiteSpace(name)) units.Add(i.ToString());
        }
        if (units.Count == 0) { units.Add("0"); units.Add("1"); }

        var pairs = new Dictionary<string, string> { ["emf_enable"] = "1", ["multicast_pass"] = "1" };
        foreach (var u in units) pairs[$"wl{u}_wmf_bss_enable"] = "1";

        await NvramSetAsync(pairs, ct);
        await NvramCommitAsync(ct);
        await Safe(Exec("for b in /sys/class/net/*/bridge/multicast_snooping; do [ -f \"$b\" ] && echo 1 > \"$b\"; done; echo snooping-on", ct));

        var restart = await SafeMsg(RestartServiceAsync("wireless", "restart", ct));
        var state = await SafeMsg(GetIgmpSnoopingAsync(ct));
        var lines = new List<string>
        {
            $"Applied IPTV multicast optimisation on {Name} ({Host})",
            $"Wireless units configured: {string.Join(", ", units.Select(u => "wl" + u))}",
        };
        if (!string.IsNullOrEmpty(ifaceOut)) lines.Add($"wl_ifnames: {ifaceOut.Trim()}");
        lines.Add("");
        lines.Add("=== wireless restart ===");
        lines.Add(restart);
        lines.Add("");
        lines.Add("=== resulting multicast state ===");
        lines.Add(state);
        return string.Join('\n', lines);
    }

    public async Task<string> SetClientIsolationAsync(string iface, bool enabled, CancellationToken ct = default)
    {
        if (!Validate.IsWlInterface(iface)) throw new McpException("Invalid wireless interface (expected wlN or wlN.M, e.g. wl0.2)");
        var val = enabled ? "1" : "0";
        await NvramSetAsync(new Dictionary<string, string> { [$"{iface}_ap_isolate"] = val }, ct);
        await NvramCommitAsync(ct);
        var live = await SafeMsg(Exec(
            $"wl -i {iface} ap_isolate {val} 2>&1; " +
            $"echo \"live=$(wl -i {iface} ap_isolate 2>/dev/null)\"; " +
            $"echo \"nvram=$(nvram get {iface}_ap_isolate)\"", ct));
        return string.Join('\n',
            $"Client isolation {(enabled ? "ENABLED" : "DISABLED")} on {iface} @ {Name} ({Host})",
            live);
    }

    // ---- Networking -------------------------------------------------------

    public Task<string> GetInterfacesAsync(CancellationToken ct = default) => Exec("ip -br addr 2>/dev/null || ifconfig", ct);

    public Task<string> GetInterfaceCountersAsync(string? iface, CancellationToken ct = default)
    {
        if (iface is not null)
        {
            var safe = Validate.Sanitize(iface);
            return Exec($"ip -s link show {safe} 2>/dev/null || ifconfig {safe}", ct);
        }
        return Exec("ip -s link 2>/dev/null || ifconfig", ct);
    }

    public Task<string> GetArpAsync(CancellationToken ct = default) => Exec("cat /proc/net/arp 2>/dev/null || arp -a", ct);
    public Task<string> GetRoutesAsync(CancellationToken ct = default) => Exec("ip route 2>/dev/null || route -n", ct);
    public Task<string> GetDhcpLeasesAsync(CancellationToken ct = default) => Exec("cat /var/lib/misc/dnsmasq.leases 2>/dev/null || cat /tmp/dnsmasq.leases 2>/dev/null || echo \"No lease file found\"", ct);

    public Task<string> PingAsync(string host, int count, CancellationToken ct = default)
    {
        var safeHost = Validate.Sanitize(host);
        var safeCount = Math.Clamp(count, 1, 20);
        if (safeHost.Length == 0) throw new McpException("Invalid host");
        return Exec($"ping -c {safeCount} -w {safeCount + 5} {safeHost}", ct);
    }

    // ---- Wireless ---------------------------------------------------------

    public async Task<List<string>> GetWirelessInterfacesAsync(CancellationToken ct = default)
    {
        var primaryOut = await Safe(Exec("nvram get wl_ifnames", ct));
        var netdevOut = await Safe(Exec("ls /sys/class/net 2>/dev/null || sed -n \"s/^ *\\([^:]*\\):.*/\\1/p\" /proc/net/dev 2>/dev/null", ct));
        var ifaces = new List<string>();
        foreach (var tok in primaryOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (Regex.IsMatch(tok, @"^(eth|ath|wl)\d+$") && !ifaces.Contains(tok)) ifaces.Add(tok);
        foreach (var tok in netdevOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (Regex.IsMatch(tok, @"^wl\d+\.\d+$") && !ifaces.Contains(tok)) ifaces.Add(tok);
        return ifaces;
    }

    public async Task<string> GetWirelessStatusAsync(CancellationToken ct = default)
    {
        var ifaces = await GetWirelessInterfacesAsync(ct);
        var wl = ifaces.Where(i => i.StartsWith("wl") || i.StartsWith("eth")).ToList();
        if (wl.Count == 0) return await Exec("wl status 2>/dev/null || echo \"wl tool not available\"", ct);
        var parts = new List<string>();
        foreach (var iface in wl)
        {
            var safe = Validate.Sanitize(iface);
            var status = await SafeMsg(Exec($"echo \"=== {safe} ===\"; wl -i {safe} status 2>/dev/null || wl -i {safe} ssid 2>/dev/null || echo \"no wl data\"", ct));
            parts.Add(status);
        }
        return string.Join("\n\n", parts);
    }

    public async Task<string> GetWirelessClientsAsync(CancellationToken ct = default)
    {
        var ifaces = await GetWirelessInterfacesAsync(ct);
        var radios = ifaces.Where(i => i.StartsWith("wl") || i.StartsWith("eth")).ToList();
        var targets = radios.Count > 0 ? radios : new List<string> { "eth1", "eth2", "wl0.1", "wl0.2" };
        var parts = new List<string>();
        foreach (var iface in targets)
        {
            var safe = Validate.Sanitize(iface);
            var cmd = string.Join(' ',
                $"ssid=$(wl -i {safe} ssid 2>/dev/null | sed -e 's/.*: //' -e 's/\"//g');",
                $"[ -z \"$ssid\" ] && ssid=$(nvram get {safe}_ssid 2>/dev/null);",
                $"echo \"=== {safe} (SSID: $ssid) ===\";",
                $"for mac in $(wl -i {safe} assoclist 2>/dev/null | awk '{{print $2}}'); do",
                "echo \"Client: $mac\";",
                $"wl -i {safe} sta_info $mac 2>/dev/null | grep -E \"rssi|rate|idle|per|tx failures|smoothed\";",
                "done");
            parts.Add(await SafeMsg(Exec(cmd, ct)));
        }
        return string.Join("\n\n", parts);
    }

    // ---- QoS / multicast --------------------------------------------------

    public async Task<string> GetQosStatusAsync(CancellationToken ct = default)
    {
        var enabled = await Safe(Exec("nvram get qos_enable", ct), "?");
        var classes = await Safe(Exec("nvram show 2>/dev/null | grep -E \"^qos_\" | sort", ct));
        var qdisc = await Safe(Exec("tc -s qdisc 2>/dev/null | head -40", ct), "tc not available");
        return string.Join('\n', $"qos_enable = {enabled}", "", "=== qos_* nvram ===", classes.Length > 0 ? classes : "(none)", "", "=== tc qdisc ===", qdisc);
    }

    public Task<string> GetQdiscStatsAsync(string iface, CancellationToken ct = default)
    {
        var safe = Validate.Sanitize(iface);
        if (safe.Length == 0) throw new McpException("Invalid interface");
        return Exec($"tc -s qdisc show dev {safe} 2>/dev/null || echo \"tc not available or no qdisc\"", ct);
    }

    public Task<string> GetIgmpSnoopingAsync(CancellationToken ct = default)
    {
        var cmd = string.Join(' ',
            "echo \"=== nvram multicast settings ===\";",
            "nvram show 2>/dev/null | grep -E \"igmp|multicast|emf|wmf|snoop\" | sort;",
            "echo \"\";",
            "echo \"=== bridge snooping (sysfs) ===\";",
            "for b in /sys/class/net/*/bridge/multicast_snooping; do",
            "  [ -f \"$b\" ] && echo \"$(echo $b | cut -d/ -f5): snooping=$(cat $b)\";",
            "done;",
            "echo \"\";",
            "echo \"=== emf (efficient multicast forwarding) ===\";",
            "emf show 2>/dev/null || echo \"emf tool not present\";");
        return Exec(cmd, ct);
    }

    // ---- Logs / diagnostics ----------------------------------------------

    public Task<string> GetSyslogAsync(int lines, CancellationToken ct = default)
    {
        var n = Math.Clamp(lines, 1, 500);
        return Exec($"logread 2>/dev/null | tail -{n} || tail -{n} /var/log/messages 2>/dev/null || dmesg | tail -{n}", ct);
    }

    public Task<string> GetConnectionsAsync(CancellationToken ct = default)
        => Exec("cat /proc/net/nf_conntrack 2>/dev/null | wc -l | xargs echo \"Active connections:\"; echo \"---\"; cat /proc/sys/net/netfilter/nf_conntrack_count 2>/dev/null", ct);

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    private static async Task<string> Safe(Task<string> task, string fallback = "") { try { return await task; } catch { return fallback; } }
    private static async Task<string> SafeMsg(Task<string> task) { try { return await task; } catch (Exception e) { return $"error: {e.Message}"; } }
}
