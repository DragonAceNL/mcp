# 90 — Consolidated missing / inconsistent features

Every gap found while reverse-engineering the TS servers, with the rewrite's
decision. "Parity" = reproduce as-is; "Fix" = correct in C#; "Add" = new;
"Defer" = documented, not implemented now.

## EdgeRouter
| # | Finding | Decision |
|---|---------|----------|
| E1 | `reboot` rejects because `sudo reboot` kills the connection | **Fix**: fire-and-forget, return "reboot issued". |
| E2 | `getConfigJson()` and `getUsers()` (`who`) have no tool (dead code) | **Add** `edgerouter_users`. Drop JSON-config variant. |
| E3 | `interface_detail` hardcodes `ethernet` — breaks for switch0/pppoe0/wg0/vlan | **Fix**: use `show interfaces <iface>` generically. |
| E4 | write tools (`firewall_add_rule`, `port_forward_add`, `dhcp_static_mapping`, `static_route_add`) don't validate interpolated values | **Fix**: light shape validation (numeric rule, MAC/IP/CIDR). |
| E5 | Errors are plain strings | **Fix**: MCP `isError` results carrying device stderr. |
| E6 | `system_resources` CPU parse fragile | **Parity** (best-effort text). |
| E7 | `restart_service` assumes `/etc/init.d/<svc>` | **Parity** + document. |

## Tomato
| # | Finding | Decision |
|---|---------|----------|
| T1 | `enable_iptv_multicast` unit discovery does N `nvram get` calls | **Parity** (validated live); tidy internally. |
| T2 | No `nvram_unset` / multi-get | **Defer** (documented). |
| T3 | `system_info` model may be "unknown" | **Parity** (best-effort). |
| T4 | Compound shell statements must end with `;` (past bug) | **Fix**: enforce in all C# shell builders. |
| T5 | Client isolation per-AP only | **Parity** + document in tool text. |

## Synology
| # | Finding | Decision |
|---|---------|----------|
| S1 | No auto-relogin on expired sid | **Add**: retry login once on auth-expired, else keep manual. |
| S2 | No inline password env | **Add** optional `SYNO_PASSWORD` (file still preferred). |
| S3 | `Camera.List` version pinned to v9 | **Fix**: clear error + point to `syno_api_info`. |
| S4 | `syno_request` params are flat only | **Parity** + document. |

## XiongMai DVR
| # | Finding | Decision |
|---|---------|----------|
| X1 | `snapshot` returns raw bytes as "jpeg" even when not FF D8 | **Fix**: `jpeg=null` unless magic bytes present. |
| X2 | KEEPALIVE (1006) defined but unused | **Defer** (documented). |
| X3 | Fixed telnet settle timing | **Parity** (validated live). |
| X4 | Vendor backdoor credential risk | **Parity**: LAN-only, `confirm=true` gated. |

## Tasmota
| # | Finding | Decision |
|---|---------|----------|
| A1 | `power` accepts numeric 1/0/2 beyond ON/OFF/TOGGLE | **Parity** + document. |
| A2 | No bulk/group power | **Defer**. |
| A3 | Ad-hoc addressing needs shared password | **Parity** + document. |

## Home Assistant
| # | Finding | Decision |
|---|---------|----------|
| H1 | No history/logbook | **Defer**. |
| H2 | `ha_light` rgb not per-channel clamped | **Fix**: clamp each 0-255. |
| H3 | Areas only via template | **Parity** + document. |

## Bugs found during live testing (2026-09-26) — all fixed

These surfaced while exercising the tools against the live devices and were fixed
in the C# rewrite (the TypeScript originals shared most of them):

| # | Server | Finding | Fix |
|---|--------|---------|-----|
| L1 | EdgeRouter | `dns_forwarding` ran `show dns forwarding` — an *"Incomplete command"* on EdgeOS v3 | Use `show dns forwarding statistics`. |
| L2 | XiongMai | `DvripClient` reused one socket with no serialisation; concurrent tool calls corrupted the framing and dropped the session | `SemaphoreSlim(1,1)` gate around every request. |
| L3 | EdgeRouter | `dhcp_static_mapping` used the network *name* as both `shared-network-name` **and** the subnet CIDR (`shared-network-name X subnet X`) — never a valid command | Take the shared-network-name and **auto-detect** the subnet CIDR from config (or accept it explicitly). |
| L4 | EdgeRouter | Network-name validation was lowercase-only (`^[a-z0-9_-]+$`), rejecting real names like `LAN`, `Guest`, `IoT` | Added a mixed-case `IsNetworkName` validator. |
| L5 | EdgeRouter (shared) | `ExecLoginShellAsync` (the `configure` PTY path) returned after ~6s idle, which fires **during** a multi-second `commit` — releasing the serialise-gate mid-commit so the next config session overlapped and clobbered it. Only the last of several rapid writes survived. | Detect completion with a sentinel echoed after `commit`/`save` (waits for `save … Done`), not a short idle window. |

## Cross-cutting decisions

- **Tool names & env vars are preserved verbatim** so the existing `mcp.json`
  only needs `command`/`args` swapped to the C# executables.
- **Server name/version** strings preserved (`<x>-mcp` / `1.0.0`).
- **Output shape preserved**: text tools return the same human text; JSON tools
  return the same pretty-printed JSON. Where a bug is fixed, the *successful*
  shape is unchanged.
- **Validation is additive** — inputs that worked before still work; only
  clearly-malformed inputs now fail fast with a clear message.
