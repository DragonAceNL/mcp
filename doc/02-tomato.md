# 02 — Tomato MCP

Manages one **or many** FreshTomato routers/APs over SSH (dropbear) — e.g. a set
of access points, each addressed by a friendly name. Typical hardware is
Broadcom-class (kernel `2.6.36.x`).

- Server name/version: `tomato-mcp` / `0.1.0`.

## Configuration (env)

Preferred: `TOMATO_ROUTERS` = JSON array, e.g.
```json
[{"name":"ap1","host":"<ap-ip>","username":"root","privateKeyPath":"<key-path>"}]
```
Each entry: `name?`, `host` (required), `port?` (22), `username?` (root),
`password?`, `privateKey?`, `privateKeyPath?`. Must have a password or a key.

Single-router fallback: `TOMATO_HOST`, `TOMATO_PORT`, `TOMATO_USER`,
`TOMATO_PASSWORD`, `TOMATO_PRIVATE_KEY`, `TOMATO_PRIVATE_KEY_PATH`, `TOMATO_NAME`.

## Connection model

`TomatoSSH` mirrors the EdgeRouter design: **one persistent connection per
router**, serialised command queue, 30 s idle disconnect, keepalive 10 s.

Key difference — **legacy dropbear algorithms** are explicitly offered so the
handshake succeeds on old Tomato builds:
- kex: curve25519-sha256(+@libssh.org), ecdh-sha2-nistp256/384/521,
  diffie-hellman-group14-sha256/sha1, diffie-hellman-group1-sha1
- host key: ssh-ed25519, ecdsa-sha2-nistp256, rsa-sha2-512/256, ssh-rsa
- cipher: aes128/192/256-ctr, aes128/256-gcm(+@openssh.com), aes128-cbc, 3des-cbc

`execInternal` is more lenient than EdgeRouter: some Tomato tools print to
stderr yet succeed, so it only rejects when `code !== 0 && errorOutput &&
!output`, and otherwise appends stderr to stdout.

`TomatoManager` holds a `TomatoSSH` per router and resolves the optional
`router` argument by name (case-insensitive) or host. With a single router and
no selector, it returns that one; otherwise it errors listing the options.

## Tool surface (23 tools)

Every device-scoped tool takes an optional `router` (name or IP).

### Read-only
| Tool | Device command(s) |
|------|-------------------|
| `tomato_list_routers` | in-memory list `name — host` |
| `tomato_system_info` | `nvram get router_name`, model probes, `os_version`/`/proc/version`, `uname -r`, `uptime` |
| `tomato_cpu_load` | `/proc/loadavg` + `top -bn1 \| head -12` |
| `tomato_memory` | `free` or `/proc/meminfo` |
| `tomato_nvram_get {key}` | `nvram get <key>` (key sanitised) |
| `tomato_nvram_show {pattern?}` | `nvram show \| grep -i <pat> \| sort` |
| `tomato_interfaces` | `ip -br addr` or `ifconfig` |
| `tomato_interface_counters {interface?}` | `ip -s link show [iface]` |
| `tomato_arp` | `/proc/net/arp` or `arp -a` |
| `tomato_routes` | `ip route` or `route -n` |
| `tomato_dhcp_leases` | `dnsmasq.leases` |
| `tomato_wireless_status` | per wl iface `wl -i <if> status/ssid` |
| `tomato_wireless_clients` | per radio: `wl assoclist` + `wl sta_info` (rssi/rate/idle/per/tx failures) |
| `tomato_qos_status` | `nvram get qos_enable`, `nvram grep ^qos_`, `tc -s qdisc` |
| `tomato_qdisc_stats {interface}` | `tc -s qdisc show dev <if>` |
| `tomato_igmp_snooping` | nvram multicast keys + sysfs snooping + `emf show` |
| `tomato_connections` | `nf_conntrack` line count + `nf_conntrack_count` |
| `tomato_logs {lines?}` | `logread \| tail` (default 50) |
| `tomato_ping {host, count?}` | `ping -c <n> -w <n+5> <host>` |

### Write / actions
| Tool | Behaviour |
|------|-----------|
| `tomato_nvram_set {pairs{}, commit?}` | key allowlist `^[A-Za-z0-9_.:-]+$`; values single-quote-escaped; optional `nvram commit` |
| `tomato_service_restart {target, action?}` | `service <target> <action>`; target `^[a-z0-9_-]+$`, action `restart\|start\|stop\|reload` |
| `tomato_enable_iptv_multicast` | sets `emf_enable=1`, `multicast_pass=1`, per-radio `wlX_wmf_bss_enable=1`; commit; bridge snooping on; `service wireless restart`; reports state |
| `tomato_set_client_isolation {interface, enabled}` | iface `^wl\d+(\.\d+)?$`; sets `<if>_ap_isolate`, commit, applies live via `wl -i <if> ap_isolate` |

### Wireless interface discovery (important subtlety)

`getWirelessInterfaces()` enumerates **both** `nvram get wl_ifnames` (primary
radios: `eth1`/`eth2` on Broadcom) **and** live netdevs under `/sys/class/net`
to pick up virtual BSSes `wl0.1`, `wl0.2`, `wl1.1` (Guest/IoT SSIDs) that are
NOT in `wl_ifnames`. Missing that step hides Guest/IoT clients. The rewrite must
keep the dual enumeration.

## Missing / inconsistent

1. **`enable_iptv_multicast` unit discovery is quadratic & fragile** — it loops
   `wl0..wl3` doing an `nvram get wlX_ifname` each. Works but noisy. Keep
   behaviour; the fix is cosmetic.
2. **No `nvram_get` for multiple keys** and no way to unset an nvram var.
   Rewrite could add `tomato_nvram_unset`; low priority — documented, not
   required for parity.
3. **`system_info` model detection** tries several nvram keys and may return
   "unknown"; acceptable, kept as best-effort.
4. **`getSyslog` bug already fixed upstream** (missing `;` after echo) — ensure
   the C# equivalents always terminate compound shell statements.
5. **Client isolation is per-AP only** — two clients on *different* APs sharing
   a VLAN still reach each other over the wired trunk. This is a documented
   limitation, surfaced in the tool description; keep it.
