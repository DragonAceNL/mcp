# mcp
# mcp

Clean C# (.NET 10) rewrites of the Model Context Protocol servers I use with
Copilot to run and troubleshoot my home LAN. These are a from-scratch
reimplementation of the original TypeScript servers in `../Tools`, sharing one
well-factored foundation instead of six copy-pasted transports.

## Servers

| Server | Transport | Manages | Project |
|--------|-----------|---------|---------|
| EdgeRouter | SSH (Vyatta/EdgeOS) | gateway: routing, NAT, firewall, DHCP, DNS, WireGuard, IPTV multicast, QoS (44 tools) | [src/EdgeRouter.Mcp](src/EdgeRouter.Mcp) |
| Tomato | SSH (dropbear, multi-router) | FreshTomato APs: WiFi, nvram, EMF/WMF IPTV, client isolation (23 tools) | [src/Tomato.Mcp](src/Tomato.Mcp) |
| Synology | HTTP (DSM WebAPI) | NAS + Surveillance Station (7 tools) | [src/Synology.Mcp](src/Synology.Mcp) |
| XiongMai DVR | TCP (DVRIP) + Telnet | analog CCTV DVR + owner recovery (13 tools) | [src/XiongMai.Mcp](src/XiongMai.Mcp) |
| Tasmota | HTTP (`/cm`) | smart plugs/switches + subnet discovery (7 tools) | [src/Tasmota.Mcp](src/Tasmota.Mcp) |
| Home Assistant | HTTP (REST `/api`) | home automation state/services (7 tools) | [src/HomeAssistant.Mcp](src/HomeAssistant.Mcp) |

Each server is a standalone stdio executable configured entirely through
environment variables — the **same names** as the original TS servers, so the
existing `mcp.json` only needs its `command`/`args` swapped.

## Documentation

- [`doc/`](doc) — per-server reference (transport, tools, env-var contract) plus
  the consolidated list of fixed inconsistencies. Public-safe: placeholder
  addresses only.

## Build

```powershell
dotnet build -c Release
```

Requires the .NET 10 SDK. Packages: `ModelContextProtocol`, `SSH.NET`,
`Microsoft.Extensions.Hosting` (central versions in `Directory.Packages.props`).

## Project layout

```
Directory.Build.props        # shared TFM (net10.0), nullable, warnings-as-errors
Directory.Packages.props     # central package versions
src/
  Mcp.Common/                # shared: EnvConfig, SshCommandSession, JsonHttpClient, Validate, McpHost
  EdgeRouter.Mcp/  Tomato.Mcp/  Synology.Mcp/
  XiongMai.Mcp/    Tasmota.Mcp/  HomeAssistant.Mcp/
tests/
  Mcp.Validate/              # live-validation harness (launches a server, lists/calls tools)
```

## Live validation

A stdio validation harness (`tests/Mcp.Validate`) launches a built server, lists
its tools, and calls read-only tools against a device. Live-testing this way
surfaced and fixed several real bugs (e.g. an SSH.NET-vs-dropbear RSA-signature
abort, and a DVRIP socket-concurrency issue).

## Migrating `mcp.json`

Point each server at its built DLL and set its `env` block, e.g.:

```json
"edgerouter": {
  "command": "dotnet",
  "args": ["<repo>/src/EdgeRouter.Mcp/bin/Release/net10.0/EdgeRouter.Mcp.dll"],
  "env": { "EDGEROUTER_HOST": "<router-ip>", "EDGEROUTER_USER": "<user>",
           "EDGEROUTER_PRIVATE_KEY_PATH": "<path-to-ssh-key>" }
}
```

## License

[MIT](LICENSE) © 2026 Dragon Ace.

You're free to use, modify, and redistribute this code — commercially or not —
**as long as you keep the copyright notice and license text** (that's the only
condition: credit the original author).
