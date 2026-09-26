# 03 — Synology MCP

Talks to Synology DSM 7 over the WebAPI (`entry.cgi`) — model/DSM version are
discovered at runtime via `syno_system_info`.

- Server name/version: `synology-mcp` / `0.1.0`.

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `SYNO_URL` | `http://<nas-ip>:5000` | DSM base URL |
| `SYNO_USER` | `admin` | DSM account |
| `SYNO_PASSWORD_FILE` | — | Path to a file holding the password (read at login) |

The password is read from `SYNO_PASSWORD_FILE` so it never passes through chat.
There is no inline-password env in the current server (only the file path).

## Protocol / client (`DsmClient`)

- All calls go through `GET {base}/webapi/{path}` with query params
  `api`, `method`, `version`, plus operation params and `_sid` once logged in.
  Default `path` is `entry.cgi`; `SYNO.API.Info` uses `query.cgi`.
- Per-request timeout via `AbortController` (default 10 s).
- **Login**: `SYNO.API.Auth` `login` v7 with `account`, `passwd`,
  `session=MCP`, `format=sid`, optional `otp_code`. On success stores
  `data.sid`. Error codes are decoded to friendly strings:

  | code | meaning |
  |------|---------|
  | 400 | No such account or incorrect password |
  | 401 | Account disabled |
  | 402 | Permission denied |
  | 403 | 2FA code required |
  | 404 | 2FA code invalid |
  | 406 | Enforce 2FA but not configured |
  | 407 | IP blocked (too many failures) |
  | 408/409/410 | password expired / must change |

- **Logout**: `SYNO.API.Auth` `logout` v7.

## Tool surface (7 tools)

| Tool | API call | Auth |
|------|----------|------|
| `syno_login {otp?}` | `SYNO.API.Auth.login` v7 | establishes session |
| `syno_logout` | `SYNO.API.Auth.logout` v7 | — |
| `syno_api_info {query?}` | `SYNO.API.Info.query` v1 (`query.cgi`) | **unauthenticated** |
| `syno_system_info` | `SYNO.Core.System.info` v1 | requires login |
| `syno_ss_info` | `SYNO.SurveillanceStation.Info.GetInfo` v1 | login + Surveillance Station |
| `syno_ss_list_cameras` | `SYNO.SurveillanceStation.Camera.List` v9 (`blFromCamList`, `privCamType=3`, `basic`, `streamInfo`) | login + SS |
| `syno_request {api, method, version, params?}` | generic escape hatch | requires login |

- `syno_login` requires `SYNO_PASSWORD_FILE` to be set (throws otherwise) and
  creates a fresh client; on failure it clears the client and throws the decoded
  error.
- `syno_api_info` works without a session (uses the existing client if present,
  else a throwaway one).
- Results are returned as pretty-printed JSON text.

## Missing / inconsistent

1. **Session is process-global, no auto-relogin** — if the sid expires, calls
   fail until `syno_login` is called again. Rewrite should optionally retry
   login once on an auth-expired error (documented behaviour; keep manual login
   as the primary flow to avoid surprising re-auth).
2. **No inline password option** — only a file. Rewrite keeps the file as the
   recommended path but also honours an optional `SYNO_PASSWORD` env for parity
   with the other servers' patterns (still discouraged).
3. **Surveillance Station version pinned** (`Camera.List` v9) — DSM upgrades can
   change the max version. Rewrite should surface a clear error and point at
   `syno_api_info` to discover the supported version range.
4. **`syno_request` params typing** — numbers/strings only; complex nested
   params must be pre-encoded. Documented limitation, kept.
5. **HTTP only (port 5000)** — no TLS. Fine on-LAN; the rewrite allows an
   `https://…:5001` base URL transparently since it just uses the configured
   URL.
