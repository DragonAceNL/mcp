# 01 — EdgeRouter MCP

Manages Ubiquiti EdgeRouter (Vyatta/EdgeOS) devices over SSH. One codebase can
drive multiple instances (e.g. a main gateway and a secondary router) — each is
a separate `mcp.json` entry with its own `EDGEROUTER_*` env.

- Server name/version: `edgerouter-mcp` / `0.1.0`.

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `EDGEROUTER_HOST` | `192.168.1.1` | Router IP |
| `EDGEROUTER_PORT` | `22` | SSH port |
| `EDGEROUTER_USER` | `ubnt` | SSH user |
| `EDGEROUTER_PASSWORD` | — | Password auth (either this or a key) |
| `EDGEROUTER_PRIVATE_KEY` | — | Inline private key |
| `EDGEROUTER_PRIVATE_KEY_PATH` | — | Path to a private key file (preferred) |

Startup fails fast if neither password nor key is present.

## Connection model (critical)

`EdgeRouterSSH` keeps **one persistent SSH connection** and **serialises every
command through a promise queue**, because EdgeOS rate-limits/blocks concurrent
sessions. Details the rewrite must replicate:

- Lazy connect; reused `Client`. `keepaliveInterval` 10 s, `readyTimeout` 20 s.
- **Idle timer**: disconnects after **30 s** of inactivity (`unref`'d so it
  never keeps the process alive).
- `exec()` chains onto `this.queue` so only one exec (or login shell) runs at a
  time; a failed command still keeps the queue chain alive.
- `execInternal`: collects stdout + stderr; on close, rejects if `code !== 0 &&
  errorOutput`, else resolves trimmed stdout.

### Two execution paths

1. **`exec(cmd)`** — a normal non-interactive `client.exec`. Used for all reads
   and diagnostics.
2. **`configure(commands, preamble?)`** — runs inside a real login shell on an
   allocated **PTY** (`vbash --login -i`, `pty: true`). This is essential:
   EdgeOS's firewall commit hook (`ubnt-fw update-rules`) only classifies a
   brand-new `firewall name` ruleset correctly from a genuine login+tty. A
   piped `echo | vbash` misfires with "Unexpected static status"/iptables
   errors. The wrapper feeds:
   ```
   source /etc/default/vyatta
   <preamble...>
   configure
   <commands...>
   commit
   save
   exit discard
   exit
   ```
   ANSI escapes are stripped from the captured output. It throws if the output
   matches `/Commit failed|Unexpected \w+ status|Configuration path: .* is not
   valid|Set failed|Delete failed/i`.
   - `preamble` lines run **before** `configure`, used to read a secret
     on-device into a shell var (e.g. `WG_PRIV=$(sudo cat ...)`) so it never
     travels over SSH.

### Command helpers

- **`show(path)`** = `/opt/vyatta/bin/vyatta-op-cmd-wrapper show <path>` (operational mode).
- **`showConfig(path)`** = `cli-shell-api showCfg <path>` (config dump).

## Tool surface (43 tools)

### System
| Tool | Device command(s) | Output |
|------|-------------------|--------|
| `edgerouter_system_info` | `cat /etc/version`, `uptime -p`, `hostname` (parallel) | parses `EdgeRouter.<model>.<version>.<build>`; returns hostname/model/version/uptime |
| `edgerouter_system_resources` | `top -bn1`, `free -m`, `df -h /` | `CPU: x%`, `Memory: used/total`, `Disk: used/total (pct)` |
| `edgerouter_get_config` | `cat /config/config.boot` | full config |
| `edgerouter_get_logs` | `tail -n <lines> /var/log/messages` (default 50) | log tail |

### Interfaces
| Tool | Command |
|------|---------|
| `edgerouter_interfaces` | `show interfaces` |
| `edgerouter_interface_detail {interface}` | `show interfaces ethernet <iface>` |
| `edgerouter_interface_counters {interface?}` | `/sbin/ip -s link show [iface]` |

### Firewall
| Tool | Behaviour |
|------|-----------|
| `edgerouter_firewall_rules {name?}` | `show firewall name <name>` or `show firewall` |
| `edgerouter_firewall_add_rule {ruleset, rule_number, action, protocol?, destination_port?, source_address?, destination_address?, description?}` | builds `set firewall name …` lines, `configure()` |
| `edgerouter_firewall_delete_rule {ruleset, rule_number}` | `delete firewall name <ruleset> rule <n>` |

### NAT / port-forward
| Tool | Behaviour |
|------|-----------|
| `edgerouter_nat_rules {type?}` | `show nat [source\|destination]` |
| `edgerouter_port_forward_add {rule_number, inbound_interface, protocol, destination_port, translation_address, translation_port?, description?}` | `set service nat rule … type destination …` |
| `edgerouter_port_forward_delete {rule_number}` | `delete service nat rule <n>` |

### DHCP
| Tool | Behaviour |
|------|-----------|
| `edgerouter_dhcp_leases` | `show dhcp leases` |
| `edgerouter_dhcp_statistics` | `show dhcp statistics` |
| `edgerouter_dhcp_static_mapping {subnet, name, mac_address, ip_address}` | two `set service dhcp-server …static-mapping…` lines |

### Routing / DNS
| Tool | Behaviour |
|------|-----------|
| `edgerouter_routes` | `show ip route` |
| `edgerouter_static_route_add {destination, next_hop}` | `set protocols static route <dest> next-hop <hop>` |
| `edgerouter_dns_forwarding` | `show dns forwarding` |

### Multicast / IPTV
| Tool | Command |
|------|---------|
| `edgerouter_igmp_proxy` | `cli-shell-api showCfg protocols igmp-proxy` |
| `edgerouter_multicast_status` | `/sbin/ip mroute show` + `head -40 /proc/net/igmp` |
| `edgerouter_igmp_snooping` | loops `/sys/class/net/*/bridge/multicast_snooping` |

### QoS
| Tool | Command |
|------|---------|
| `edgerouter_qos_config` | `showCfg traffic-policy` + `showCfg traffic-control` |
| `edgerouter_qdisc_stats {interface}` | `/sbin/tc -s qdisc show dev <iface>` |

### VPN / WireGuard
| Tool | Behaviour |
|------|-----------|
| `edgerouter_vpn_status {type?}` | `show vpn ipsec sa` and/or `show openvpn status` (both, guarded, when `all`) |
| `edgerouter_wireguard_status {interface?}` | `sudo wg show [iface]` |
| `edgerouter_wireguard_genkey {key_path?}` | mkdir; `wg genkey \| tee` if absent; returns `wg pubkey`. Default path `/config/auth/wg_priv.key`. Idempotent, chmod 600. |
| `edgerouter_wireguard_configure {interface, address, private_key_path?, listen_port?, description?, peer_public_key, peer_allowed_ips[], peer_endpoint?, peer_keepalive?, peer_description?}` | validates all inputs; `set interfaces wireguard …` with `private-key "$WG_PRIV"` and preamble `WG_PRIV=$(sudo cat <path>)` |

### Diagnostics
| Tool | Command |
|------|---------|
| `edgerouter_cpu_load` | `/proc/loadavg` + `grep -c ^processor /proc/cpuinfo` |
| `edgerouter_offload_status` | `nf_conntrack_count` + `showCfg system offload` |
| `edgerouter_show_config {path}` | `cli-shell-api showCfg <path>` |
| `edgerouter_ping {host, count?}` | `ping -c <n> <host>` |
| `edgerouter_traceroute {host}` | `traceroute <host>` |
| `edgerouter_nslookup {host}` | `nslookup <host>` |
| `edgerouter_port_scan {host, ports[], timeout?}` | BusyBox-safe `nc -w<t>` loop, summarises OPEN ports |
| `edgerouter_arp` | `show arp` |

### Advanced / management
| Tool | Behaviour |
|------|-----------|
| `edgerouter_exec {command}` | raw command (queued) |
| `edgerouter_configure {commands[]}` | `configure()` wrapper (auto begin/commit/save) |
| `edgerouter_add_ssh_key {user, key_id, type, key}` | validates; `set system login user … authentication public-keys …` |
| `edgerouter_list_ssh_keys {user}` | `showCfg system login user <u> authentication public-keys` |
| `edgerouter_delete_ssh_key {user, key_id}` | `delete … public-keys <key_id>` |
| `edgerouter_restart_service {service}` | `sudo /etc/init.d/<svc> restart` |
| `edgerouter_reboot {confirm}` | requires `confirm=true`; `sudo reboot` |

## Input validation / security

- `portScan`, `wireguardConfigure`, `wireguardGenKey`, `addSshPublicKey`,
  `listSshPublicKeys`, `deleteSshPublicKey` all apply strict regex allowlists to
  interpolated values (host, iface, CIDR, key type, base64 body, paths).
- WireGuard/SSH-key secrets are read on-device via preamble; private keys never
  cross the wire.

## Missing / inconsistent (see also [90-inconsistencies.md](90-inconsistencies.md))

1. **`edgerouter_reboot` returns nothing useful** — `sudo reboot` drops the
   connection; the queued exec usually rejects. Rewrite should fire-and-forget
   and return a clear "reboot issued" message.
2. **`getConfigJson` / `getUsers` (`who`) exist in the client but have no
   tool** — dead code. Rewrite: either expose `edgerouter_users`/JSON config or
   drop. Decision: expose `edgerouter_users` (useful) and a `format` option is
   out of scope.
3. **`edgerouter_interface_detail` hardcodes `ethernet`** — fails for
   `switch0`, `pppoe0`, `wg0`, `vlan`/`bond` etc. Rewrite should detect the
   interface family or fall back to `show interfaces <iface>`.
4. **`restart_service` path assumes `/etc/init.d/<svc>`** — inconsistent with
   EdgeOS services managed by other means; document the limitation.
5. **No structured error typing** — every failure is a plain string. Rewrite
   returns MCP `isError` results with the device stderr preserved.
6. **`system_resources` CPU parse is fragile** (`top` locale/format). Keep the
   command but treat output as best-effort text.
7. **Validation gaps**: `firewall_add_rule`, `port_forward_add`,
   `dhcp_static_mapping`, `static_route_add` interpolate values into `set`
   lines **without** allowlist validation (they rely on the config parser to
   reject bad input). Rewrite adds light validation (rule numbers numeric,
   MAC/IP/CIDR shape) to fail fast and avoid odd shell edge cases.
