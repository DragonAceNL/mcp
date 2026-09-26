using System.ComponentModel;
using Mcp.Common.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EdgeRouter.Mcp;

[McpServerToolType]
public sealed class EdgeRouterTools(EdgeRouterClient r)
{
    // ===== System =====

    [McpServerTool(Name = "edgerouter_system_info")]
    [Description("Get EdgeRouter system information including version, hostname, model, and uptime")]
    public async Task<string> SystemInfo(CancellationToken ct)
    {
        var i = await r.GetSystemInfoAsync(ct);
        return $"Hostname: {i.Hostname}\nModel: {i.Model}\nVersion: {i.Version}\nUptime: {i.Uptime}";
    }

    [McpServerTool(Name = "edgerouter_system_resources")]
    [Description("Get CPU, memory, and disk usage statistics")]
    public Task<string> SystemResources(CancellationToken ct) => r.GetSystemResourcesAsync(ct);

    [McpServerTool(Name = "edgerouter_get_config")]
    [Description("Get the full running configuration (config.boot)")]
    public Task<string> GetConfig(CancellationToken ct) => r.GetConfigAsync(ct);

    [McpServerTool(Name = "edgerouter_get_logs")]
    [Description("Get recent system log entries")]
    public Task<string> GetLogs([Description("Number of log lines to retrieve (default: 50)")] int lines = 50, CancellationToken ct = default)
        => r.GetLogsAsync(lines, ct);

    [McpServerTool(Name = "edgerouter_users")]
    [Description("List currently logged-in users/sessions on the router (who)")]
    public Task<string> Users(CancellationToken ct) => r.GetUsersAsync(ct);

    // ===== Interfaces =====

    [McpServerTool(Name = "edgerouter_interfaces")]
    [Description("List all network interfaces and their status")]
    public Task<string> Interfaces(CancellationToken ct) => r.GetInterfacesAsync(ct);

    [McpServerTool(Name = "edgerouter_interface_detail")]
    [Description("Get detailed information about a specific interface")]
    public Task<string> InterfaceDetail([Description("Interface name (e.g., eth0, switch0, pppoe0, wg0)")] string @interface, CancellationToken ct = default)
        => r.GetInterfaceDetailAsync(Validate.Require(@interface, Validate.IsInterface, "Invalid interface name"), ct);

    [McpServerTool(Name = "edgerouter_interface_counters")]
    [Description("Get low-level interface counters including RX/TX errors and drops (ip -s link). Omit interface for all.")]
    public Task<string> InterfaceCounters([Description("Interface name (optional, shows all if omitted)")] string? @interface = null, CancellationToken ct = default)
        => r.GetInterfaceCountersAsync(@interface is null ? null : Validate.Require(@interface, Validate.IsInterface, "Invalid interface name"), ct);

    // ===== Firewall =====

    [McpServerTool(Name = "edgerouter_firewall_rules")]
    [Description("Get firewall rules. Optionally specify a ruleset name.")]
    public Task<string> FirewallRules([Description("Firewall ruleset name (optional, shows all if omitted)")] string? name = null, CancellationToken ct = default)
        => r.GetFirewallRulesAsync(name, ct);

    [McpServerTool(Name = "edgerouter_firewall_add_rule")]
    [Description("Add a firewall rule to a ruleset")]
    public async Task<string> FirewallAddRule(
        [Description("Firewall ruleset name (e.g., WAN_IN, WAN_LOCAL)")] string ruleset,
        [Description("Rule number (lower = higher priority)")] int rule_number,
        [Description("Action to take: accept, drop, reject")] string action,
        [Description("Protocol (tcp, udp, icmp, all)")] string? protocol = null,
        [Description("Destination port or range (e.g., 80, 80-443)")] string? destination_port = null,
        [Description("Source IP/network (optional)")] string? source_address = null,
        [Description("Destination IP/network (optional)")] string? destination_address = null,
        [Description("Rule description")] string? description = null,
        CancellationToken ct = default)
    {
        if (action is not ("accept" or "drop" or "reject")) throw new McpException("action must be accept, drop, or reject");
        var b = $"firewall name {ruleset} rule {rule_number}";
        var cmds = new List<string> { $"set {b} action {action}" };
        if (protocol is not null) cmds.Add($"set {b} protocol {protocol}");
        if (destination_port is not null) cmds.Add($"set {b} destination port {destination_port}");
        if (source_address is not null) cmds.Add($"set {b} source address {source_address}");
        if (destination_address is not null) cmds.Add($"set {b} destination address {destination_address}");
        if (description is not null) cmds.Add($"set {b} description \"{description}\"");
        var res = await r.ConfigureAsync(cmds, ct: ct);
        return string.IsNullOrEmpty(res) ? $"Firewall rule {rule_number} added to {ruleset}" : res;
    }

    [McpServerTool(Name = "edgerouter_firewall_delete_rule")]
    [Description("Delete a firewall rule from a ruleset")]
    public async Task<string> FirewallDeleteRule(
        [Description("Firewall ruleset name")] string ruleset,
        [Description("Rule number to delete")] int rule_number,
        CancellationToken ct = default)
    {
        var res = await r.ConfigureAsync(new[] { $"delete firewall name {ruleset} rule {rule_number}" }, ct: ct);
        return string.IsNullOrEmpty(res) ? $"Firewall rule {rule_number} deleted from {ruleset}" : res;
    }

    // ===== NAT / port-forward =====

    [McpServerTool(Name = "edgerouter_nat_rules")]
    [Description("Get NAT rules (source and/or destination NAT)")]
    public Task<string> NatRules([Description("NAT type to show: source, destination, all")] string? type = null, CancellationToken ct = default)
        => r.GetNatRulesAsync(type, ct);

    [McpServerTool(Name = "edgerouter_port_forward_add")]
    [Description("Add a port forwarding rule (destination NAT)")]
    public async Task<string> PortForwardAdd(
        [Description("Rule number")] int rule_number,
        [Description("Inbound interface (e.g., eth0)")] string inbound_interface,
        [Description("Protocol: tcp, udp, tcp_udp")] string protocol,
        [Description("External port or range")] string destination_port,
        [Description("Internal IP address")] string translation_address,
        [Description("Internal port (optional, same as dest if omitted)")] string? translation_port = null,
        [Description("Rule description")] string? description = null,
        CancellationToken ct = default)
    {
        Validate.Require(inbound_interface, Validate.IsInterface, "Invalid inbound interface");
        Validate.Require(translation_address, Validate.IsIpv4, "Invalid translation address (expected IPv4)");
        if (protocol is not ("tcp" or "udp" or "tcp_udp")) throw new McpException("protocol must be tcp, udp, or tcp_udp");
        var b = $"service nat rule {rule_number}";
        var cmds = new List<string>
        {
            $"set {b} type destination",
            $"set {b} inbound-interface {inbound_interface}",
            $"set {b} protocol {protocol}",
            $"set {b} destination port {destination_port}",
            $"set {b} inside-address address {translation_address}",
        };
        if (translation_port is not null) cmds.Add($"set {b} inside-address port {translation_port}");
        if (description is not null) cmds.Add($"set {b} description \"{description}\"");
        var res = await r.ConfigureAsync(cmds, ct: ct);
        return string.IsNullOrEmpty(res) ? $"Port forward rule {rule_number} added" : res;
    }

    [McpServerTool(Name = "edgerouter_port_forward_delete")]
    [Description("Delete a port forwarding rule")]
    public async Task<string> PortForwardDelete([Description("Rule number to delete")] int rule_number, CancellationToken ct = default)
    {
        var res = await r.ConfigureAsync(new[] { $"delete service nat rule {rule_number}" }, ct: ct);
        return string.IsNullOrEmpty(res) ? $"NAT rule {rule_number} deleted" : res;
    }

    // ===== DHCP =====

    [McpServerTool(Name = "edgerouter_dhcp_leases")]
    [Description("Get current DHCP leases")]
    public Task<string> DhcpLeases(CancellationToken ct) => r.GetDhcpLeasesAsync(ct);

    [McpServerTool(Name = "edgerouter_dhcp_statistics")]
    [Description("Get DHCP server statistics")]
    public Task<string> DhcpStatistics(CancellationToken ct) => r.GetDhcpStatisticsAsync(ct);

    [McpServerTool(Name = "edgerouter_dhcp_static_mapping")]
    [Description("Add a static DHCP mapping (IP reservation). The subnet CIDR is auto-detected from the shared-network-name unless you pass it explicitly.")]
    public async Task<string> DhcpStaticMapping(
        [Description("DHCP shared-network-name (e.g., LAN, Guest, IoT)")] string network,
        [Description("Mapping name/hostname (e.g., my-nas)")] string name,
        [Description("MAC address (format: xx:xx:xx:xx:xx:xx)")] string mac_address,
        [Description("IP address to reserve")] string ip_address,
        [Description("Subnet CIDR (e.g. 192.168.1.0/24). Auto-detected from the network name if omitted.")] string? subnet = null,
        CancellationToken ct = default)
    {
        Validate.Require(network, Validate.IsNetworkName, "Invalid network name");
        Validate.Require(name, Validate.IsKeyId, "Invalid mapping name");
        Validate.Require(mac_address, Validate.IsMac, "Invalid MAC address (expected xx:xx:xx:xx:xx:xx)");
        Validate.Require(ip_address, Validate.IsIpv4, "Invalid IP address (expected IPv4)");
        var cidr = subnet ?? await r.GetDhcpSubnetCidrAsync(network, ct);
        Validate.Require(cidr, Validate.IsCidr, "Invalid subnet CIDR (expected e.g. 192.168.1.0/24)");
        var b = $"service dhcp-server shared-network-name {network} subnet {cidr} static-mapping {name}";
        var res = await r.ConfigureAsync(new[] { $"set {b} mac-address {mac_address}", $"set {b} ip-address {ip_address}" }, ct: ct);
        return string.IsNullOrEmpty(res) ? $"Static mapping {name} added ({ip_address} @ {network} {cidr})" : res;
    }

    // ===== Routing / DNS =====

    [McpServerTool(Name = "edgerouter_routes")]
    [Description("Get routing table")]
    public Task<string> Routes(CancellationToken ct) => r.GetRoutesAsync(ct);

    [McpServerTool(Name = "edgerouter_static_route_add")]
    [Description("Add a static route")]
    public async Task<string> StaticRouteAdd(
        [Description("Destination network (CIDR format, e.g., 10.0.0.0/24)")] string destination,
        [Description("Next hop IP address or interface")] string next_hop,
        CancellationToken ct = default)
    {
        Validate.Require(destination, Validate.IsCidr, "Invalid destination (expected CIDR, e.g. 10.0.0.0/24)");
        var res = await r.ConfigureAsync(new[] { $"set protocols static route {destination} next-hop {next_hop}" }, ct: ct);
        return string.IsNullOrEmpty(res) ? $"Static route to {destination} via {next_hop} added" : res;
    }

    [McpServerTool(Name = "edgerouter_dns_forwarding")]
    [Description("Get DNS forwarding configuration and statistics")]
    public Task<string> DnsForwarding(CancellationToken ct) => r.GetDnsForwardingAsync(ct);

    // ===== Multicast / IPTV =====

    [McpServerTool(Name = "edgerouter_igmp_proxy")]
    [Description("Get IGMP proxy configuration (used for IPTV multicast routing)")]
    public Task<string> IgmpProxy(CancellationToken ct) => r.GetIgmpProxyConfigAsync(ct);

    [McpServerTool(Name = "edgerouter_multicast_status")]
    [Description("Get live multicast routing table and IGMP group memberships (useful for diagnosing IPTV)")]
    public Task<string> MulticastStatus(CancellationToken ct) => r.GetMulticastStatusAsync(ct);

    [McpServerTool(Name = "edgerouter_igmp_snooping")]
    [Description("Get IGMP snooping state for internal switch/bridges (multicast flooding causes IPTV stutter if off)")]
    public Task<string> IgmpSnooping(CancellationToken ct) => r.GetIgmpSnoopingAsync(ct);

    // ===== QoS =====

    [McpServerTool(Name = "edgerouter_qos_config")]
    [Description("Get configured QoS / traffic-policy / smart-queue settings")]
    public Task<string> QosConfig(CancellationToken ct) => r.GetQosConfigAsync(ct);

    [McpServerTool(Name = "edgerouter_qdisc_stats")]
    [Description("Get live queueing discipline stats for an interface (tc -s qdisc), showing drops/overlimits/congestion")]
    public Task<string> QdiscStats([Description("Interface name (e.g., pppoe0, switch0, eth0.4)")] string @interface, CancellationToken ct = default)
        => r.GetQdiscStatsAsync(Validate.Require(@interface, Validate.IsInterface, "Invalid interface name"), ct);

    // ===== VPN =====

    [McpServerTool(Name = "edgerouter_vpn_status")]
    [Description("Get VPN status (IPsec and/or OpenVPN)")]
    public async Task<string> VpnStatus([Description("VPN type to check: ipsec, openvpn, all")] string? type = null, CancellationToken ct = default)
    {
        if (type == "ipsec") return await r.GetVpnIpsecAsync(ct);
        if (type == "openvpn") return await r.GetVpnOpenvpnAsync(ct);
        var ipsec = await Safe(r.GetVpnIpsecAsync(ct), "No IPsec tunnels");
        var openvpn = await Safe(r.GetVpnOpenvpnAsync(ct), "No OpenVPN connections");
        return $"=== IPsec ===\n{ipsec}\n\n=== OpenVPN ===\n{openvpn}";
    }

    // ===== WireGuard =====

    [McpServerTool(Name = "edgerouter_wireguard_status")]
    [Description("Get live WireGuard status (handshakes, transfer, endpoints) via \"wg show\". Optionally scope to one interface.")]
    public Task<string> WireguardStatus([Description("WireGuard interface name (e.g. wg0). Omit for all.")] string? @interface = null, CancellationToken ct = default)
        => r.WireguardStatusAsync(@interface, ct);

    [McpServerTool(Name = "edgerouter_wireguard_genkey")]
    [Description("Generate (if absent) a WireGuard private key on-device and return its PUBLIC key. Private key never leaves the router. Idempotent.")]
    public async Task<string> WireguardGenkey([Description("On-device path to store the private key (default: /config/auth/wg_priv.key)")] string? key_path = null, CancellationToken ct = default)
        => $"Public key: {await r.WireguardGenKeyAsync(key_path, ct)}";

    [McpServerTool(Name = "edgerouter_wireguard_configure")]
    [Description("Configure a WireGuard interface and one peer in a single commit. Reads the private key on-device from private_key_path (kept off the wire). Uses a proper vyatta config session so the WireGuard commit hook succeeds.")]
    public async Task<string> WireguardConfigure(
        [Description("WireGuard interface name (e.g. wg0)")] string @interface,
        [Description("Interface tunnel address in CIDR (e.g. 10.0.0.1/24)")] string address,
        [Description("Public key of the remote peer")] string peer_public_key,
        [Description("Allowed IPs / routed networks for the peer (CIDR list)")] string[] peer_allowed_ips,
        [Description("On-device path of the private key (default: /config/auth/wg_priv.key)")] string? private_key_path = null,
        [Description("UDP listen port (server side; omit for client-only)")] int? listen_port = null,
        [Description("Interface description")] string? description = null,
        [Description("Remote peer endpoint host:port (client side; omit on server)")] string? peer_endpoint = null,
        [Description("Persistent keepalive seconds (e.g. 25; recommended behind NAT/CGNAT)")] int? peer_keepalive = null,
        [Description("Peer description")] string? peer_description = null,
        CancellationToken ct = default)
    {
        var res = await r.WireguardConfigureAsync(new EdgeRouterClient.WgConfig(
            @interface, address, private_key_path, listen_port, description,
            peer_public_key, peer_allowed_ips, peer_endpoint, peer_keepalive, peer_description), ct);
        return string.IsNullOrEmpty(res) ? $"WireGuard {@interface} configured with peer" : res;
    }

    // ===== Diagnostics =====

    [McpServerTool(Name = "edgerouter_cpu_load")]
    [Description("Get CPU load average and core count")]
    public Task<string> CpuLoad(CancellationToken ct) => r.GetCpuLoadAsync(ct);

    [McpServerTool(Name = "edgerouter_offload_status")]
    [Description("Get hardware-offload (hwnat) config and active conntrack connection count")]
    public Task<string> OffloadStatus(CancellationToken ct) => r.GetOffloadStatusAsync(ct);

    [McpServerTool(Name = "edgerouter_show_config")]
    [Description("Show any part of the running configuration by path (e.g. \"service nat\", \"interfaces ethernet eth0\")")]
    public Task<string> ShowConfig([Description("Configuration path to show (space-separated, e.g. \"service dhcp-server\")")] string path, CancellationToken ct = default)
        => r.ShowConfigAsync(path, ct);

    [McpServerTool(Name = "edgerouter_ping")]
    [Description("Ping a host from the router")]
    public Task<string> Ping([Description("Host to ping (IP or hostname)")] string host, [Description("Number of pings (default: 4)")] int count = 4, CancellationToken ct = default)
        => r.PingAsync(Validate.Require(host, Validate.IsHost, "Invalid host"), count, ct);

    [McpServerTool(Name = "edgerouter_traceroute")]
    [Description("Traceroute to a host from the router")]
    public Task<string> Traceroute([Description("Host to traceroute (IP or hostname)")] string host, CancellationToken ct = default)
        => r.TracerouteAsync(Validate.Require(host, Validate.IsHost, "Invalid host"), ct);

    [McpServerTool(Name = "edgerouter_nslookup")]
    [Description("DNS lookup from the router")]
    public Task<string> Nslookup([Description("Hostname to lookup")] string host, CancellationToken ct = default)
        => r.NslookupAsync(Validate.Require(host, Validate.IsHost, "Invalid host"), ct);

    [McpServerTool(Name = "edgerouter_port_scan")]
    [Description("TCP port scan of a host from the router (reliable on BusyBox: uses nc -w, not -z/timeout). Reports which ports are open vs closed/filtered.")]
    public Task<string> PortScan(
        [Description("Target host to scan (IP or hostname)")] string host,
        [Description("Ports to check, e.g. [22, 80, 443, 554, 34567, 37777]")] int[] ports,
        [Description("Per-port connect timeout in seconds (1-10, default 2)")] int timeout = 2,
        CancellationToken ct = default)
        => r.PortScanAsync(host, ports, timeout, ct);

    [McpServerTool(Name = "edgerouter_arp")]
    [Description("Get ARP table")]
    public Task<string> Arp(CancellationToken ct) => r.GetArpAsync(ct);

    // ===== Advanced / management =====

    [McpServerTool(Name = "edgerouter_exec")]
    [Description("Execute a raw command on the EdgeRouter (use with caution)")]
    public Task<string> Exec([Description("Command to execute")] string command, CancellationToken ct = default)
        => r.ExecAsync(command, ct);

    [McpServerTool(Name = "edgerouter_configure")]
    [Description("Execute configuration commands (automatically handles begin/commit/save)")]
    public async Task<string> Configure([Description("Array of configuration commands (without begin/commit/save)")] string[] commands, CancellationToken ct = default)
    {
        var res = await r.ConfigureAsync(commands, ct: ct);
        return string.IsNullOrEmpty(res) ? "Configuration applied successfully" : res;
    }

    [McpServerTool(Name = "edgerouter_add_ssh_key")]
    [Description("Add an SSH public key to a login user so the router accepts key-based login (commits and saves).")]
    public async Task<string> AddSshKey(
        [Description("Login user to attach the key to (e.g. Ace)")] string user,
        [Description("Identifier/name for the key (the key comment, e.g. router-mcp)")] string key_id,
        [Description("Key type (e.g. ssh-rsa, ssh-ed25519)")] string type,
        [Description("The base64 key body only (WITHOUT the type prefix or trailing comment)")] string key,
        CancellationToken ct = default)
    {
        var res = await r.AddSshPublicKeyAsync(user, key_id, type, key, ct);
        return string.IsNullOrEmpty(res) ? $"SSH key \"{key_id}\" added to user {user}" : res;
    }

    [McpServerTool(Name = "edgerouter_list_ssh_keys")]
    [Description("List the configured SSH public keys for a login user")]
    public Task<string> ListSshKeys([Description("Login user (e.g. Ace)")] string user, CancellationToken ct = default)
        => r.ListSshPublicKeysAsync(user, ct);

    [McpServerTool(Name = "edgerouter_delete_ssh_key")]
    [Description("Remove a configured SSH public key from a login user (commits and saves)")]
    public async Task<string> DeleteSshKey(
        [Description("Login user (e.g. Ace)")] string user,
        [Description("Identifier/name of the key to remove")] string key_id,
        CancellationToken ct = default)
    {
        var res = await r.DeleteSshPublicKeyAsync(user, key_id, ct);
        return string.IsNullOrEmpty(res) ? $"SSH key \"{key_id}\" removed from user {user}" : res;
    }

    [McpServerTool(Name = "edgerouter_restart_service")]
    [Description("Restart a system service")]
    public Task<string> RestartService([Description("Service name (e.g., dnsmasq, dhcpd, openvpn)")] string service, CancellationToken ct = default)
        => r.RestartServiceAsync(Validate.Require(service, Validate.IsServiceTarget, "Invalid service name"), ct);

    [McpServerTool(Name = "edgerouter_reboot")]
    [Description("Reboot the EdgeRouter (use with extreme caution!)")]
    public Task<string> Reboot([Description("Must be true to confirm reboot")] bool confirm, CancellationToken ct = default)
        => confirm ? r.RebootAsync(ct) : throw new McpException("Refusing to reboot: pass confirm=true.");

    private static async Task<string> Safe(Task<string> task, string fallback)
    {
        try { return await task; } catch { return fallback; }
    }
}
