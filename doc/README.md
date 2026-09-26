# MCP servers — reference (`doc/`)

Public-safe reference for the six MCP servers in this repository: each server's
transport, connection model, the full tool surface (name, inputs, device
commands issued, output shape), the security model, and the known
inconsistencies with the rewrite's decision for each.

> Addresses here are **placeholders** (e.g. `<router-ip>`, `192.168.1.1`). The
> servers take all real hosts/credentials from environment variables at runtime;
> nothing environment-specific is baked into the code or these docs.

| # | Server | Transport | Tools | Page |
|---|--------|-----------|-------|------|
| 1 | EdgeRouter | SSH (Vyatta/EdgeOS) | 44 | [01-edgerouter.md](01-edgerouter.md) |
| 2 | Tomato | SSH (dropbear, multi-router) | 23 | [02-tomato.md](02-tomato.md) |
| 3 | Synology | HTTP (DSM WebAPI) | 7 | [03-synology.md](03-synology.md) |
| 4 | XiongMai DVR | TCP (DVRIP) + Telnet | 13 | [04-xiongmai-dvr.md](04-xiongmai-dvr.md) |
| 5 | Tasmota | HTTP (`/cm`) | 7 | [05-tasmota.md](05-tasmota.md) |
| 6 | Home Assistant | HTTP (REST `/api`) | 7 | [06-homeassistant.md](06-homeassistant.md) |

- [90-inconsistencies.md](90-inconsistencies.md) — consolidated list of every
  missing / inconsistent / wrong behaviour found (in the original TypeScript
  servers and during live testing) and the rewrite's decision for each.

## Origin

These are clean C# (.NET) rewrites of an earlier set of personal TypeScript MCP
servers, sharing one well-factored foundation instead of six copy-pasted
transports. Tool names and env-var contracts were preserved so an existing
`mcp.json` only needs its `command`/`args` swapped to the C# executables.

## Runtime shape

Each server is a standalone stdio executable configured entirely through
environment variables (documented per page). Runtime wiring lives in the user
`mcp.json` (`%APPDATA%\Code\User\mcp.json`): every server is a separate process
talking MCP over **stdio**.
