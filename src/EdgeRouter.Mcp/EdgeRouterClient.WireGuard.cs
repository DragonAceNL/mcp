using System.Text.RegularExpressions;
using Mcp.Common.Text;
using ModelContextProtocol;

namespace EdgeRouter.Mcp;

public sealed partial class EdgeRouterClient
{
    // ---- WireGuard --------------------------------------------------------

    public Task<string> WireguardStatusAsync(string? iface, CancellationToken ct = default)
    {
        if (iface is not null && !Regex.IsMatch(iface, "^[A-Za-z0-9._-]+$"))
            throw new McpException("Invalid interface name");
        var target = iface is not null ? $" {iface}" : "";
        return ExecAsync($"sudo wg show{target} 2>/dev/null || echo \"(no WireGuard interfaces up)\"", ct);
    }

    public async Task<string> WireguardGenKeyAsync(string? keyPath, CancellationToken ct = default)
    {
        var path = keyPath ?? "/config/auth/wg_priv.key";
        if (!Validate.IsKeyPath(path)) throw new McpException("Invalid key path");
        var dir = Regex.Replace(path, "/[^/]*$", "");
        var script = string.Join('\n',
            $"sudo mkdir -p {dir}",
            $"if [ ! -f {path} ]; then wg genkey | sudo tee {path} >/dev/null; sudo chmod 600 {path}; fi",
            $"sudo cat {path} | wg pubkey");
        return (await ExecAsync(script, ct)).Trim();
    }

    public sealed record WgConfig(
        string Iface, string Address, string? PrivateKeyPath, int? ListenPort, string? Description,
        string PeerPublicKey, IReadOnlyList<string> PeerAllowedIps, string? PeerEndpoint, int? PeerKeepalive, string? PeerDescription);

    public Task<string> WireguardConfigureAsync(WgConfig o, CancellationToken ct = default)
    {
        var iface = string.IsNullOrEmpty(o.Iface) ? "wg0" : o.Iface;
        if (!Validate.IsWgInterface(iface)) throw new McpException("Invalid WireGuard interface name (expected wgN)");
        if (!Validate.IsCidr(o.Address)) throw new McpException("Invalid address (expected CIDR, e.g. 10.0.0.1/24)");
        if (!Regex.IsMatch(o.PeerPublicKey, "^[A-Za-z0-9+/=]{43,44}$")) throw new McpException("Invalid peer public key");
        var keyPath = o.PrivateKeyPath ?? "/config/auth/wg_priv.key";
        if (!Validate.IsKeyPath(keyPath)) throw new McpException("Invalid private key path");
        foreach (var ip in o.PeerAllowedIps)
            if (!Validate.IsCidr(ip)) throw new McpException($"Invalid allowed-ips entry: {ip}");
        if (o.PeerEndpoint is not null && !Validate.IsEndpoint(o.PeerEndpoint)) throw new McpException("Invalid peer endpoint (expected host:port)");

        var b = $"interfaces wireguard {iface}";
        var peer = $"{b} peer {o.PeerPublicKey}";
        var commands = new List<string>
        {
            $"set {b} address {o.Address}",
            $"set {b} private-key \"$WG_PRIV\"",
        };
        if (o.ListenPort is not null) commands.Add($"set {b} listen-port {o.ListenPort}");
        if (o.Description is not null) commands.Add($"set {b} description \"{o.Description}\"");
        foreach (var ip in o.PeerAllowedIps) commands.Add($"set {peer} allowed-ips {ip}");
        if (o.PeerEndpoint is not null) commands.Add($"set {peer} endpoint {o.PeerEndpoint}");
        if (o.PeerKeepalive is not null) commands.Add($"set {peer} persistent-keepalive {o.PeerKeepalive}");
        if (o.PeerDescription is not null) commands.Add($"set {peer} description \"{o.PeerDescription}\"");

        var preamble = new[] { $"WG_PRIV=$(sudo cat {keyPath})" };
        return ConfigureAsync(commands, preamble, ct);
    }

    // ---- Port scan (BusyBox-safe) ----------------------------------------

    public async Task<string> PortScanAsync(string host, IReadOnlyList<int> ports, int timeoutSec, CancellationToken ct = default)
    {
        if (!Validate.IsHost(host)) throw new McpException($"Invalid host: {host}");
        var cleanPorts = ports.Where(p => p is >= 1 and <= 65535).ToList();
        if (cleanPorts.Count == 0) throw new McpException("No valid ports (1-65535) provided");
        var t = Math.Clamp(timeoutSec, 1, 10);
        var portList = string.Join(' ', cleanPorts);
        var script =
            $"for p in {portList}; do " +
            $"if nc -w{t} {host} \"$p\" </dev/null >/dev/null 2>&1; then " +
            "echo \"$p open\"; else echo \"$p closed/filtered\"; fi; done";
        var outp = await ExecAsync(script, ct);
        var open = Regex.Matches(outp, @"^(\d+) open$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var summary = open.Count > 0 ? $"OPEN: {string.Join(", ", open)}" : "No open ports found (all closed/filtered)";
        return $"{host} (timeout {t}s)\n{outp.Trim()}\n---\n{summary}";
    }

    // ---- SSH key management ----------------------------------------------

    public Task<string> AddSshPublicKeyAsync(string user, string keyId, string type, string key, CancellationToken ct = default)
    {
        if (!Validate.IsUserName(user)) throw new McpException("Invalid user name");
        if (!Validate.IsKeyId(keyId)) throw new McpException("Invalid key identifier");
        if (!Validate.IsSshKeyType(type)) throw new McpException("Invalid key type");
        var cleanKey = key.Trim();
        if (!Validate.IsBase64Body(cleanKey)) throw new McpException("Invalid public key body (expected base64, no type prefix or comment)");
        var b = $"system login user {user} authentication public-keys {keyId}";
        return ConfigureAsync(new[] { $"set {b} type {type}", $"set {b} key {cleanKey}" }, ct: ct);
    }

    public Task<string> ListSshPublicKeysAsync(string user, CancellationToken ct = default)
    {
        if (!Validate.IsUserName(user)) throw new McpException("Invalid user name");
        return ExecAsync($"cli-shell-api showCfg system login user {user} authentication public-keys 2>/dev/null || echo \"(none)\"", ct);
    }

    public Task<string> DeleteSshPublicKeyAsync(string user, string keyId, CancellationToken ct = default)
    {
        if (!Validate.IsUserName(user)) throw new McpException("Invalid user name");
        if (!Validate.IsKeyId(keyId)) throw new McpException("Invalid key identifier");
        return ConfigureAsync(new[] { $"delete system login user {user} authentication public-keys {keyId}" }, ct: ct);
    }
}
