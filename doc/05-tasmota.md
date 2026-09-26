# 05 — Tasmota MCP

Controls Tasmota smart plugs/switches over the HTTP command API
(`http://<host>/cm?cmnd=<command>`). No SSH. Devices are discovered by scanning
one or more configured subnets (`TASMOTA_SCAN`).

- Server name/version: `tasmota-mcp` / `0.1.0`.

## Configuration (env)

| Var | Meaning |
|-----|---------|
| `TASMOTA_DEVICES` | JSON array of `{name?, host, password?, passwordFile?, user?}` |
| `TASMOTA_HOST` / `TASMOTA_NAME` / `TASMOTA_USER` | single-device fallback |
| `TASMOTA_PASSWORD` / `TASMOTA_PASSWORD_FILE` | shared default web-admin password |
| `TASMOTA_SCAN` | comma-separated CIDR/range/IP list to scan |
| `TASMOTA_SCAN_CONCURRENCY` | default 128 |
| `TASMOTA_SCAN_TIMEOUT_MS` | default 800 |
| `TASMOTA_CACHE_FILE` | default `~/.tasmota-mcp-cache.json` |

At least one of `TASMOTA_DEVICES`, `TASMOTA_HOST`, or `TASMOTA_SCAN` is required.

## Client (`TasmotaClient` / `TasmotaManager`)

- **`command(cmnd)`** — URL-encodes `cmnd`; if a password is configured, adds
  `user` (default `admin`) + `password` query params. Parses JSON, falls back to
  raw text. 10 s timeout. Command length capped at 512 chars. No shell surface
  (pure HTTP query), so no injection risk.
- **`applyConfiguredWebPassword()`** — sends `WebPassword <configured pw>`; the
  secret comes only from local config, never a tool argument.
- **`TasmotaManager`**:
  - `explicit` devices from config + `discovered` devices from scan/cache.
  - Credentials always come from the shared default (discovered devices never
    store secrets).
  - **Cache**: names+hosts persisted to `TASMOTA_CACHE_FILE`
    (`{scannedAt, devices:[{name,host}]}`), loaded at startup.
  - `get(nameOrHost)` resolves by name, then host, then — for any valid
    IP/hostname — an **ad-hoc client** using the shared default creds. So any
    device is reachable without a config entry if the shared password matches.
  - `discover()` expands `TASMOTA_SCAN` targets (`expandTargets`: CIDR /8–/32,
    `a-b` range, or single IP; capped at 2048), probes each with
    `Status 5` (StatusNET → Hostname), registers hits by hostname, rewrites the
    cache.

## Tool surface (7 tools)

| Tool | Behaviour |
|------|-----------|
| `tasmota_list_devices` | list `name — host [config\|discovered]` |
| `tasmota_status {device?}` | `Status 0`; formats DeviceName, host/IP, MAC, module/FW, Wi-Fi SSID/RSSI/channel, relay POWER states, uptime |
| `tasmota_command {device?, command}` | raw Tasmota command; returns JSON or text |
| `tasmota_power {device?, output?, state?}` | `Power<output> <state>`; output 0=all,1-8; state ON/OFF/TOGGLE (default TOGGLE) |
| `tasmota_set_name {device?, hostname?, deviceName?, friendlyName?}` | `Hostname`, `DeviceName`, `FriendlyName1`; hostname validated `^[A-Za-z0-9-]{1,32}$` |
| `tasmota_set_web_password {device?}` | applies the device's configured password via `WebPassword` |
| `tasmota_discover` | scans `TASMOTA_SCAN`, registers devices by hostname |

`device` is optional when exactly one device is configured; otherwise required
(name or IP). Errors return `isError`.

## Missing / inconsistent

1. **`tasmota_power` state regex allows `1/0/2`** but the tool description only
   says ON/OFF/TOGGLE. Keep numeric acceptance (Tasmota supports it) but
   document it.
2. **Discovery cache stores only name+host** — intentional (no secrets). Keep.
3. **No bulk/group operations** (e.g. all relays off across devices). Rewrite
   could add a group power tool; out of parity scope, noted as a future
   enhancement.
4. **Ad-hoc addressing depends on the shared password** — a device with a
   different password than the shared default can't be reached ad-hoc. Document
   that per-device passwords require an explicit `TASMOTA_DEVICES` entry.
5. **`Status 0` field access is defensive** (`asRecord`) — good; the rewrite
   mirrors the null-safe extraction so odd firmwares don't crash the tool.
