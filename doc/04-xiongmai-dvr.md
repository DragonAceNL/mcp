# 04 — XiongMai DVR MCP

Controls a XiongMai "Sofia" DVR/NVR over its native **DVRIP** protocol
(TCP/34567) with a **Telnet** (TCP/23) root-shell fallback for owner-authorised
recovery. Typical hardware is a HiSilicon-based analog DVR.

- Server name/version: `xiongmai-dvr-mcp` / `0.1.0`.

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `DVR_HOST` | `<dvr-ip>` | DVR IP |
| `DVR_PORT` | `34567` | DVRIP port |
| `DVR_USER` | `admin` | DVRIP user |
| `DVR_PASSWORD` | — | DVRIP password (optional; may be passed per-call) |
| `DVR_TELNET_USER` | `root` | Telnet user (vendor backdoor) |
| `DVR_TELNET_PASS` | `xc3511` | Telnet password |
| `DVR_TELNET_PORT` | `23` | Telnet port |

## DVRIP protocol (`DvripClient`)

**Wire format** — 20-byte little-endian header + JSON payload + `0x0a 0x00`:

| offset | field |
|--------|-------|
| 0 | `0xff` head flag |
| 1 | `0x00` version |
| 2–3 | reserved |
| 4–7 | session id (u32 LE) |
| 8–11 | sequence (u32 LE) |
| 12–13 | reserved (channel/total) |
| 14–15 | message code (u16 LE) |
| 16–19 | payload length (u32 LE) = JSON bytes + 2 |

Response parsing: read 20-byte header, `respMsg = u16@14`, `expected = u32@16`,
accumulate until `20 + expected` bytes, strip trailing `\x00`/`\x0a`, JSON-parse
(binary payloads like snapshots stay raw).

**Sofia password hash** — `md5(password)` → 8 chars: for `i` in 0..7,
`n = (md5[2i] + md5[2i+1]) % 62`, index into
`0-9A-Za-z`. The login sends `{EncryptType:MD5, LoginType:'DVRIP-Web',
UserName, PassWord: sofiaHash(pw)}`.

**Message codes**: LOGIN 1000, LOGOUT 1002, KEEPALIVE 1006, SYSINFO 1020,
CONFIG_GET 1042, CHANNELTITLE_GET 1048, ABILITY_GET 1360, OPMACHINE 1450,
OPTIMEQUERY 1452, SNAP 1560, USERS 1472.

**Return codes**: 100 OK, 101 unknown, 102 version, 103 illegal, 104 already
logged in, 105 not logged in, 106 user/pw error, 107 no permission, 108 timeout,
113 unsupported, **203 wrong password**, **204 no such user**, **205 account
locked**, 206 blacklisted.

Session id is stored as hex (`sessionIdHex`) and auto-added as `SessionID` to
later payloads.

## Telnet root shell (`TelnetClient`)

- Handles IAC negotiation: reply `DO→WONT`, `WILL→DONT`, skip `SB…SE`.
- Drives `login:` then `Password:` prompts, waits ~1.5 s for the shell banner.
- **Command completion via sentinel**: sends `<cmd>; echo __XM""_DONE_5C21__`.
  The `""` splits the marker so the shell's **echo of the command line** does
  not itself contain the literal sentinel — completion is detected only from the
  executed `echo` output. The first captured line (the command echo) is dropped.
- `execAll(cmds)` runs commands sequentially; `fireAndForget(cmd)` sends without
  waiting (for `reboot -f`).

## Recovery (`recovery.ts`)

`resetAccountPassword(t, account, newPassword)`:
1. `cat /mnt/mtd/Config/Account1` (JSON; each user has a Sofia-hash `Password`).
2. Extract the target account's current hash; back up the file to `.bak`.
3. `sed -i 's#<oldHash>#<newHash>#[g]' Account1` (hashes are base62, safe in sed).
4. Re-read and verify the new hash is present; if not, restore backup and fail.
5. `sync` then `reboot -f` (hard reboot so the running app can't rewrite the
   file from its in-memory copy on a clean shutdown).

## Tool surface (13 tools)

| Tool | Path | Notes |
|------|------|-------|
| `dvr_login {host?,port?,user?,password?}` | DVRIP | establishes reused session; decodes Ret |
| `dvr_try_passwords {host?,port?,user?,passwords[]}` | DVRIP | tries each; stops on success or 205/206 lock; keeps a working session |
| `dvr_system_info {name?}` | DVRIP | SystemInfo / StorageInfo / WorkState |
| `dvr_config_get {name}` | DVRIP | e.g. `General`, `NetWork.NetCommon` |
| `dvr_get_time` | DVRIP | OPTimeQuery |
| `dvr_channel_titles` | DVRIP | ChannelTitle |
| `dvr_users` | DVRIP | Users + groups |
| `dvr_snapshot {channel?, outputPath}` | DVRIP | OPSNAP; writes JPEG if returned, else JSON error |
| `dvr_command {code, payload}` | DVRIP | raw escape hatch; SessionID auto-added |
| `dvr_reboot {confirm}` | DVRIP | OPMachine; requires `confirm=true` |
| `dvr_hash {password}` | local | compute Sofia hash (no network) |
| `dvr_shell {host?,port?,user?,password?,commands[]}` | Telnet | root BusyBox shell |
| `dvr_reset_password {newPassword, account?, confirm}` | Telnet | owner recovery; requires `confirm=true` |

Results are JSON text; errors return `isError`.

## Missing / inconsistent

1. **`snapshot` logic bug** — the "not JPEG" branch still returns
   `jpeg: resp.raw` (`jpeg: isJpeg ? resp.raw : resp.raw`), so a non-JPEG
   binary is treated as an image. Rewrite: return `jpeg=null` when the magic
   bytes `FF D8` are absent, and surface the raw bytes/JSON instead.
2. **No keepalive** — long-idle DVRIP sessions can drop; there is a KEEPALIVE
   (1006) code defined but unused. Rewrite may add an optional keepalive tool or
   send-on-demand; low priority, documented.
3. **`login` timeout is fixed 6 s** — fine, but the rewrite exposes it via the
   existing per-request timeout.
4. **Telnet `ready` uses a fixed 1.5 s settle** — works but timing-sensitive.
   Keep, with the same tuning, since it is validated against the live unit.
5. **Security**: the vendor backdoor credential is a real risk. The rewrite
   keeps it strictly for owner-authorised LAN recovery, gated behind explicit
   `confirm=true` on the destructive tools, exactly as today.
