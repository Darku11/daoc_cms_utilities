# AldhranConsole

AldhranConsole is the authenticated HTTP service used by DAoC CMS for live game-server actions. It accepts requests from the CMS on port `5100` and forwards core actions to `AldhranBridge.cs` over its TCP protocol on port `2000`.

```text
DAoC CMS -> AldhranConsole -> AldhranBridge.cs -> DOL / OpenDAoC
             HTTP :5100         TCP :2000
```

The service has no compile-time dependency on Dawn of Light or OpenDAoC assemblies. Core-specific behavior stays inside `AldhranBridge.cs`, whose compatibility layer supports both server implementations. AldhranConsole also uses the shared game-database tables needed for item lookup, itemshop delivery, zone shortcuts and World Forge data.

## Requirements

- .NET 10 SDK to build the project
- .NET 10 ASP.NET Core Runtime to run a framework-dependent release
- MySQL or MariaDB access to the game database
- `AldhranBridge.cs` installed and running for live game-world actions

.NET 10 is the current LTS target of this project.

## Configuration

Copy and edit `appsettings.json` before starting the service:

| Key | Purpose |
| --- | --- |
| `Console:ListenUrl` | Local HTTP address. Defaults to `http://127.0.0.1:5100`. |
| `Console:SharedSecret` | Authenticates both CMS HTTP requests and the TCP connection to AldhranBridge. |
| `Console:DbConnection` | Connection string for the DOL/OpenDAoC game database. |
| `Console:BridgeHost` | Host running the game server and AldhranBridge. |
| `Console:BridgePort` | AldhranBridge TCP port. Defaults to `2000`. |
| `Console:BridgeTimeoutSeconds` | Maximum wait for one bridge request. Defaults to `8`. |
| `Console:ScriptsPath` | Optional target directory used by the World Forge upload endpoint. |

ASP.NET Core environment-variable overrides use double underscores, for example
`Console__SharedSecret` and `Console__DbConnection`. This is preferable to writing production secrets into a distributed configuration file.

Use the exact same secret in:

- DAoC CMS: `game_server_shared_secret`
- AldhranConsole: `Console:SharedSecret`
- Game server: `SharedSecret` in `config/daoc_cms_bridge.conf`

The CMS setup and ACP generate `daoc_cms_bridge.conf` for AldhranBridge, CMSLiveEvents and
GuildChatBridge. Their C# source files contain no site-specific secret, URL or port.

Existing installations can still use the former `Console:ApiSecret`, `Console:BridgeSecret`, `Console:DolHost` and `Console:DolPort` keys. New installations should use the keys shown above.

The service refuses to start while a secret or database connection is missing or still contains a shipped placeholder.

## Build and run

Development build:

```bash
dotnet restore
dotnet build --configuration Release
dotnet run --configuration Release
```

Framework-dependent Windows release:

```bash
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output publish/win-x64
```

Framework-dependent Linux release:

```bash
dotnet publish --configuration Release --runtime linux-x64 --self-contained false --output publish/linux-x64
```

Copy the published directory as one unit, edit its `appsettings.json`, and run `AldhranConsole.exe` on Windows or `dotnet AldhranConsole.dll` on Linux. Source checkouts and release archives should never contain `bin/`, `obj/` or a configured production `appsettings.json`.

### Linux service example

After publishing to `/opt/aldhran-console`, a minimal `/etc/systemd/system/aldhran-console.service` can use:

```ini
[Unit]
Description=AldhranConsole
After=network.target mariadb.service

[Service]
Type=simple
User=daoc
WorkingDirectory=/opt/aldhran-console
ExecStart=/usr/bin/dotnet /opt/aldhran-console/AldhranConsole.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=-/etc/aldhran-console.env
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

Then run `systemctl daemon-reload`, `systemctl enable --now aldhran-console`, and verify `/health`. Restrict the environment file and configured application directory to the service account.

## Health and connection checks

The liveness endpoint does not require a secret and exposes no connection details:

```bash
curl http://127.0.0.1:5100/health
```

All functional endpoints require the shared secret:

```bash
curl -H "X-Aldhran-Secret: YOUR_SHARED_SECRET" http://127.0.0.1:5100/status
```

`/health` confirms that the HTTP process is running. `/status` additionally confirms that AldhranConsole can authenticate to and receive a response from AldhranBridge.
If the database is temporarily unavailable during startup, the service logs the failure and keeps bridge-only endpoints available. Restart it after the database connection is restored so the auxiliary schema check can complete.

## Endpoints

| Endpoint | Method | Purpose | Execution path |
| --- | --- | --- | --- |
| `/health` | GET | Unauthenticated liveness probe | Console only |
| `/status` | GET | Server and online-player status | AldhranBridge |
| `/kick` | POST | Disconnect a player | AldhranBridge |
| `/privlevel` | POST | Set an online account privilege level from 0 to 3 | AldhranBridge |
| `/teleport` | POST | Move a player to coordinates or a configured zone shortcut | Database lookup, then AldhranBridge |
| `/giveitem` | POST | Deliver an item to an online player | AldhranBridge |
| `/items/search` | GET | Search item templates for ACP autocomplete | Game database |
| `/setstats` | POST | Change supported live character stats | AldhranBridge |
| `/heal`, `/revive` | POST | Restore an online character | AldhranBridge |
| `/freeze`, `/mute` | POST | Toggle moderation states | AldhranBridge |
| `/broadcast`, `/guildchat` | POST | Send game messages | AldhranBridge |
| `/restart` | POST | Schedule the game-server stop used by the CMS restart workflow | AldhranBridge |
| `/raw` | POST | Execute a restricted raw command through an online GM account | AldhranBridge |
| `/shop/cm-listings` | GET | List consignment merchant offers | Game database |
| `/shop/purchase` | POST | Validate, charge and deliver an itemshop purchase | Game database and AldhranBridge |
| `/world-forge/*` | GET / POST | World Forge synchronization and optional script upload | Game database or configured filesystem path |

The former `/gmmode` route was removed because no matching AldhranBridge action existed and DAoC CMS does not call it.

## Security

- Keep the default loopback binding when the CMS and Console run on the same host.
- Do not expose port `5100` directly to the public internet. For a remote CMS, restrict it by firewall and place TLS in front of the service.
- Use a unique randomly generated shared secret and never commit the configured value.
- Grant the database user only the permissions required by the enabled Console features.
- Leave `Console:ScriptsPath` empty unless World Forge uploads are intentionally enabled.
- The `/raw` endpoint remains restricted to CMS SuperAdmins and independently blocks shutdown commands in both the Console and AldhranBridge.

## License

Licensed under the GNU General Public License v3.0. See the repository `LICENSE` file.
