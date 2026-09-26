# 06 — Home Assistant MCP

Talks to Home Assistant over its REST API (`/api`) with a Long-Lived Access
Token. One codebase can drive multiple instances (each a separate `mcp.json`
entry with its own `HASS_URL` + token file).

- Server name/version: `homeassistant-mcp` / `0.1.0`.

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `HASS_URL` | `http://homeassistant.local:8123` | base URL |
| `HASS_TOKEN_FILE` | — | path to a file with the token (preferred) |
| `HASS_TOKEN` | — | inline token (fallback) |
| `HASS_TIMEOUT_MS` | 10000 | per-request timeout |

Token loading fails fast if neither file nor inline token is present.

## Client (`HassClient`)

Thin wrapper over `fetch` with `Authorization: Bearer <token>` and
`Content-Type: application/json`. `AbortController` timeout. Non-OK responses
throw `HTTP <status> <text>`; a 401 adds a "check the token" hint. Empty bodies
return `null`; non-JSON bodies (e.g. `/api/` → "API running.", `/api/template`
→ plain text) are returned as text.

Endpoints used:
- `GET /api/` — ping ("API running.")
- `GET /api/config` — version, location_name, state, unit system
- `GET /api/states` — all entities
- `GET /api/states/<entity_id>` — one entity
- `GET /api/services` — service domains
- `POST /api/services/<domain>/<service>` — call a service (returns changed states)
- `POST /api/template` — render a Jinja2 template (plain text)

## Tool surface (7 tools)

| Tool | Behaviour |
|------|-----------|
| `ha_check` | ping + `/config`; reports connected URL, version, name, state |
| `ha_list_entities {domain?, search?}` | `/states`; filter by domain prefix and/or case-insensitive substring over entity_id + friendly_name; sorted `entity_id = state (friendly)` |
| `ha_get_state {entity_id}` | full entity JSON |
| `ha_call_service {domain, service, entity_id?, data?}` | merges `data`; `entity_id` split on commas into an array; returns changed states |
| `ha_light {entity_id, action?, brightness_pct?, color_temp_kelvin?, rgb?}` | maps on/off/toggle → turn_on/turn_off/toggle; on `turn_on` adds brightness_pct (clamped 0-100), color_temp_kelvin, rgb_color |
| `ha_list_services {domain?}` | lists `domain: svc1, svc2, …` |
| `ha_template {template}` | renders Jinja2, returns text |

Errors return `isError`.

## Missing / inconsistent

1. **No history / logbook access** — `/api/history` and `/api/logbook` aren't
   exposed. Rewrite could add read-only history; out of parity scope, noted.
2. **`ha_light` rgb validation is loose** — accepts any 3-number array without
   range-clamping each channel. Rewrite clamps each to 0-255.
3. **No area/device convenience beyond templates** — areas are only reachable
   via `ha_template` (`{{ areas() }}`). Documented; kept.
4. **Two instances share a server name** (`homeassistant-mcp`) — fine over
   stdio (separate processes), but the rewrite includes the base URL in the
   startup log to disambiguate, as the TS version already does.
5. **Service call result can be large** (`/states` deltas). Kept as-is;
   pretty-printed JSON.
