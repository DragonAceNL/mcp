using System.Text.RegularExpressions;
using Mcp.Common.Ssh;

namespace EdgeRouter.Mcp;

/// <summary>
/// SSH client for Ubiquiti EdgeRouter (Vyatta/EdgeOS). Wraps the shared
/// serialised SSH session and reproduces every helper from the TS
/// <c>edgerouter-ssh.ts</c>, including the login-shell/PTY configure() path that
/// EdgeOS's firewall commit hook requires.
/// </summary>
public sealed partial class EdgeRouterClient(SshCommandSession ssh)
{
    private static readonly Regex ConfigFailure =
        new(@"Commit failed|Unexpected \w+ status|Configuration path: .* is not valid|Set failed|Delete failed",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<string> ExecAsync(string command, CancellationToken ct = default) => ssh.ExecAsync(command, ct);

    /// <summary>Operational-mode show: vyatta-op-cmd-wrapper show &lt;path&gt;.</summary>
    public Task<string> ShowAsync(string path, CancellationToken ct = default)
        => ssh.ExecAsync($"/opt/vyatta/bin/vyatta-op-cmd-wrapper show {path}", ct);

    /// <summary>Config dump: cli-shell-api showCfg &lt;path&gt;.</summary>
    public Task<string> ShowCfgAsync(string path, CancellationToken ct = default)
        => ssh.ExecAsync($"cli-shell-api showCfg {path}", ct);

    /// <summary>
    /// Run configuration commands inside a real vyatta login shell on a PTY.
    /// Optional preamble lines run before <c>configure</c> (e.g. to read a secret
    /// on-device into a shell variable).
    /// </summary>
    public async Task<string> ConfigureAsync(IReadOnlyList<string> commands, IReadOnlyList<string>? preamble = null, CancellationToken ct = default)
    {
        var lines = new List<string> { "source /etc/default/vyatta" };
        if (preamble is not null) lines.AddRange(preamble);
        lines.Add("configure");
        lines.AddRange(commands);
        lines.Add("commit");
        lines.Add("save");
        // Leave config mode; the shell session's own `exit` (added by
        // ExecLoginShellAsync after its completion sentinel) closes the login shell.
        lines.Add("exit discard");

        var output = await ssh.ExecLoginShellAsync(lines, "vbash --login -i", ct).ConfigureAwait(false);
        if (ConfigFailure.IsMatch(output))
            throw new InvalidOperationException($"Configuration failed:\n{output}");
        return output;
    }

    // ---- System -----------------------------------------------------------

    public async Task<(string Version, string Uptime, string Hostname, string Model)> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var version = await SafeAsync(ExecAsync("cat /etc/version", ct), "unknown");
        var uptime = await SafeAsync(ExecAsync("uptime -p", ct), "unknown");
        var hostname = await SafeAsync(ExecAsync("hostname", ct), "unknown");

        var model = "EdgeRouter";
        var cleanVersion = version.Trim();
        var m = Regex.Match(version.Trim(), @"^EdgeRouter\.([^.]+)\.(v.+?)\.\d{6,}");
        if (m.Success) { model = m.Groups[1].Value; cleanVersion = m.Groups[2].Value; }
        return (cleanVersion, uptime.Trim(), hostname.Trim(), model.Trim());
    }

    public async Task<string> GetSystemResourcesAsync(CancellationToken ct = default)
    {
        var cpu = await ExecAsync("top -bn1 | grep 'Cpu(s)' | awk '{print $2 + $4}'", ct);
        var memory = await ExecAsync("free -m | awk 'NR==2{printf \"%s/%sMB (%.2f%%)\", $3,$2,$3*100/$2}'", ct);
        var disk = await ExecAsync("df -h / | awk 'NR==2{print $3\"/\"$2\" (\"$5\")\"}'", ct);
        return $"CPU: {cpu.Trim()}%\nMemory: {memory.Trim()}\nDisk: {disk.Trim()}";
    }

    public Task<string> GetConfigAsync(CancellationToken ct = default) => ExecAsync("cat /config/config.boot", ct);
    public Task<string> GetLogsAsync(int lines, CancellationToken ct = default) => ExecAsync($"tail -n {lines} /var/log/messages", ct);
    public Task<string> GetUsersAsync(CancellationToken ct = default) => ExecAsync("who", ct);

    // ---- Interfaces -------------------------------------------------------

    public Task<string> GetInterfacesAsync(CancellationToken ct = default) => ShowAsync("interfaces", ct);
    // Fix E3: generic — do NOT hardcode "ethernet"; works for switch0/pppoe0/wg0/vlan.
    public Task<string> GetInterfaceDetailAsync(string iface, CancellationToken ct = default) => ShowAsync($"interfaces {iface}", ct);
    public Task<string> GetInterfaceCountersAsync(string? iface, CancellationToken ct = default)
        => ExecAsync(iface is not null ? $"/sbin/ip -s link show {iface}" : "/sbin/ip -s link show", ct);

    // ---- Firewall / NAT ---------------------------------------------------

    public Task<string> GetFirewallRulesAsync(string? name, CancellationToken ct = default)
        => ShowAsync(name is not null ? $"firewall name {name}" : "firewall", ct);

    public Task<string> GetNatRulesAsync(string? type, CancellationToken ct = default)
        => ShowAsync(type switch { "source" => "nat source", "destination" => "nat destination", _ => "nat" }, ct);

    // ---- DHCP / routing / DNS --------------------------------------------

    public Task<string> GetDhcpLeasesAsync(CancellationToken ct = default) => ShowAsync("dhcp leases", ct);
    public Task<string> GetDhcpStatisticsAsync(CancellationToken ct = default) => ShowAsync("dhcp statistics", ct);

    /// <summary>Resolve the subnet CIDR configured under a DHCP shared-network-name (e.g. LAN -> 192.168.1.0/24).</summary>
    public async Task<string> GetDhcpSubnetCidrAsync(string network, CancellationToken ct = default)
    {
        var cfg = await ExecAsync($"cli-shell-api showCfg service dhcp-server shared-network-name {network}", ct);
        var m = Regex.Match(cfg, @"subnet\s+([0-9.]+/[0-9]{1,2})");
        if (!m.Success)
            throw new InvalidOperationException($"could not find a subnet CIDR for shared-network-name '{network}'");
        return m.Groups[1].Value;
    }
    public Task<string> GetRoutesAsync(CancellationToken ct = default) => ShowAsync("ip route", ct);
    // EdgeOS requires a subcommand; "dns forwarding" alone is an "Incomplete command".
    public Task<string> GetDnsForwardingAsync(CancellationToken ct = default) => ShowAsync("dns forwarding statistics", ct);
    public Task<string> GetArpAsync(CancellationToken ct = default) => ShowAsync("arp", ct);

    // ---- Multicast / IPTV -------------------------------------------------

    public Task<string> GetIgmpProxyConfigAsync(CancellationToken ct = default) => ExecAsync("cli-shell-api showCfg protocols igmp-proxy", ct);

    public async Task<string> GetMulticastStatusAsync(CancellationToken ct = default)
    {
        var mroute = await SafeAsync(ExecAsync("/sbin/ip mroute show 2>/dev/null || echo \"(no mroute entries)\"", ct), "(unavailable)");
        var igmp = await SafeAsync(ExecAsync("cat /proc/net/igmp 2>/dev/null | head -40 || echo '(no igmp data)'", ct), "(unavailable)");
        return $"=== Multicast Routes (ip mroute) ===\n{mroute}\n\n=== IGMP Memberships (/proc/net/igmp) ===\n{igmp}";
    }

    public Task<string> GetIgmpSnoopingAsync(CancellationToken ct = default)
        => ExecAsync(
            "for d in /sys/class/net/*/bridge/multicast_snooping; do " +
            "if [ -f \"$d\" ]; then dev=$(echo \"$d\" | cut -d/ -f5); " +
            "echo \"$dev: snooping=$(cat \"$d\")\"; fi; done 2>/dev/null || echo \"No bridge multicast_snooping entries found\"", ct);

    // ---- QoS --------------------------------------------------------------

    public async Task<string> GetQosConfigAsync(CancellationToken ct = default)
    {
        var tp = await SafeAsync(ExecAsync("cli-shell-api showCfg traffic-policy 2>/dev/null || echo \"(none)\"", ct), "(none)");
        var sq = await SafeAsync(ExecAsync("cli-shell-api showCfg traffic-control 2>/dev/null || echo \"(none)\"", ct), "(none)");
        return $"=== traffic-policy ===\n{tp}\n\n=== traffic-control (smart-queue) ===\n{sq}";
    }

    public Task<string> GetQdiscStatsAsync(string iface, CancellationToken ct = default) => ExecAsync($"/sbin/tc -s qdisc show dev {iface}", ct);

    // ---- Diagnostics ------------------------------------------------------

    public async Task<string> GetCpuLoadAsync(CancellationToken ct = default)
    {
        var loadavg = await ExecAsync("cat /proc/loadavg", ct);
        var cores = await ExecAsync("grep -c ^processor /proc/cpuinfo", ct);
        var parts = loadavg.Split(' ');
        var la = string.Join(" / ", parts.Take(3));
        return $"Load average (1/5/15 min): {la}\nCPU cores: {cores.Trim()}";
    }

    public async Task<string> GetOffloadStatusAsync(CancellationToken ct = default)
    {
        var conntrack = await SafeAsync(ExecAsync("cat /proc/sys/net/netfilter/nf_conntrack_count 2>/dev/null || echo \"?\"", ct), "?");
        var offload = await SafeAsync(ExecAsync("cli-shell-api showCfg system offload 2>/dev/null || echo \"(default)\"", ct), "(default)");
        return $"Active tracked connections: {conntrack.Trim()}\n\n=== Offload config ===\n{offload}";
    }

    public Task<string> ShowConfigAsync(string path, CancellationToken ct = default) => ExecAsync($"cli-shell-api showCfg {path}", ct);
    public Task<string> PingAsync(string host, int count, CancellationToken ct = default) => ExecAsync($"ping -c {count} {host}", ct);
    public Task<string> TracerouteAsync(string host, CancellationToken ct = default) => ExecAsync($"traceroute {host}", ct);
    public Task<string> NslookupAsync(string host, CancellationToken ct = default) => ExecAsync($"nslookup {host}", ct);

    // ---- VPN --------------------------------------------------------------

    public Task<string> GetVpnIpsecAsync(CancellationToken ct = default) => ShowAsync("vpn ipsec sa", ct);
    public Task<string> GetVpnOpenvpnAsync(CancellationToken ct = default) => ShowAsync("openvpn status", ct);

    // ---- Management -------------------------------------------------------

    public Task<string> RestartServiceAsync(string service, CancellationToken ct = default) => ExecAsync($"sudo /etc/init.d/{service} restart", ct);

    // Fix E1: reboot drops the connection; fire it and don't await a clean exit.
    public async Task<string> RebootAsync(CancellationToken ct = default)
    {
        try { await ExecAsync("sudo reboot", ct); }
        catch { /* the connection drops as the router goes down — expected */ }
        ssh.Disconnect();
        return "Reboot issued. The router will drop offline shortly.";
    }

    private static async Task<string> SafeAsync(Task<string> task, string fallback)
    {
        try { return await task; } catch { return fallback; }
    }
}
