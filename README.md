# DAoC CMS Utilities

Server-side and supporting components for [DAoC CMS](https://github.com/Darku11/daoc_cms) — the pieces that don't live inside the CMS web application itself, because they run on or alongside the game server instead.

None of this is required for a basic DAoC CMS installation. Each component only matters if you're using the CMS feature it powers. See `daoc_cms`'s [`setup/steps/bridges.php`](https://github.com/Darku11/daoc_cms/blob/main/setup/steps/bridges.php) (the "Bridges" step of the installer) for how these pieces fit together end to end.

## Components

### AldhranBridge.cs
The in-game console bridge. Runs as a script inside your DOL or OpenDAoC server's `scripts/` folder and is the only thing that actually touches players, items, and the world on behalf of the CMS — status, kick, privlevel, teleport, item delivery, guild chat relay, restart, and more. Listens on the configured TCP port (2000 by default) for commands forwarded by AldhranConsole.

Runs unmodified on both **DOL** and **OpenDAoC** — the two forks' diverging APIs are resolved at load time through a small reflection-based compatibility shim (`ServerCoreCompat`) inside the file.

### AldhranConsole/
The HTTP bridge between the DAoC CMS website and AldhranBridge.cs. A standalone ASP.NET Core (`net10.0`) minimal-API service — not a scripts-folder drop-in, it runs as its own process anywhere it can reach both the CMS and the game server. The CMS talks to it over HTTP (port 5100, `X-Aldhran-Secret` header); it forwards admin/console actions to AldhranBridge.cs over TCP and talks to the game database directly for the itemshop, item-autocomplete, zone and World Forge endpoints.

It doesn't reference any DOL/OpenDAoC server assemblies. Core-specific live actions are handled by AldhranBridge's compatibility layer, while the Console uses database tables shared by the supported DOL and OpenDAoC schemas. The same Console build therefore runs with **either** server implementation.

Copy `appsettings.json` and fill in `SharedSecret` and `DbConnection` before running it — the shipped file only contains placeholders. `SharedSecret` must match `game_server_shared_secret` in the CMS and `SharedSecret` in the generated `daoc_cms_bridge.conf`.

See [`AldhranConsole/README.md`](AldhranConsole/README.md) for configuration, build, publishing, endpoint and security instructions.

### CMSLiveEvents.cs
Pushes PvP kill and keep-capture events from the game server to the CMS's live event feed (`api_events.php`). Drop-in script for the `scripts/` folder, runs unmodified on DOL and OpenDAoC.

### GuildChatBridge.cs
Relays in-game guild chat to Discord by re-registering the `&gu` command. Drop-in script for the `scripts/` folder, runs unmodified on DOL and OpenDAoC.

### DAoCCmsBridgeConfig.cs
Required shared configuration reader for the three game-server bridge scripts. Put it in the same `scripts/` folder. The CMS setup or ACP generates `daoc_cms_bridge.conf`; place that file in the game server's `config/` directory. AldhranBridge, CMSLiveEvents and GuildChatBridge then use the same CMS API URL, shared secret and TCP port without source edits. Replacing only the configuration file requires a server restart but no script rebuild.

### database.sql
The DAoC CMS database schema (CMS-side tables, not the game server database).

### TerrainService-RC1-win-x64.zip
Supporting terrain/map data service used by some admin tools. Only needed if a tool you're running depends on it.

## Deployment

This section describes the complete bridge deployment for both **DOL** and **OpenDAoC**. The
paths below use a Windows release at `C:\DAoC\Release` as an example; replace that path with the
actual root directory containing the game-server executable, `scripts/`, `config/` and `lib/`.

### 1. Choose the required components

| File or service | Required when | Destination |
| --- | --- | --- |
| `DAoCCmsBridgeConfig.cs` | Any game-server bridge is enabled | `<game-server-root>/scripts/` |
| `AldhranBridge.cs` | CMS console actions, Discord-to-game guild chat, item delivery or live administration are enabled | `<game-server-root>/scripts/` |
| `GuildChatBridge.cs` | Game-to-Discord guild chat is enabled | `<game-server-root>/scripts/` |
| `CMSLiveEvents.cs` | PvP and keep events should be sent to the CMS | `<game-server-root>/scripts/` |
| `daoc_cms_bridge.conf` | Any of the C# scripts above is installed | `<game-server-root>/config/` |
| AldhranConsole | `AldhranBridge.cs` is used | Separate .NET service; do not copy it into `scripts/` |
| Discord bot | Discord commands or either guild-chat direction is used | Node.js process configured through the CMS ACP |

Installing all four C# files is recommended. Site-specific values belong only in
`daoc_cms_bridge.conf` and AldhranConsole's `appsettings.json`; do not edit constants in the C#
sources.

### 2. Generate the shared game-server configuration

During a new CMS installation, complete the **Bridges** setup step. For an existing installation,
open **ACP → General Settings → Game Server → Bridge Connection**, save these values and select
**Download bridge config**:

| CMS field | Value |
| --- | --- |
| Bridge Port | TCP port used by `AldhranBridge.cs`; default `2000` |
| CMS Event API | Absolute public URL ending in `/api_events.php` |
| Shared Secret | One random secret shared by the CMS, AldhranConsole and the game server |
| Console Host | Host or IP of AldhranConsole as seen by the web server |
| Console Port | AldhranConsole HTTP port; default `5100` |

The downloaded file has this format:

```ini
ConfigVersion=1
CmsApiUrl=https://example.com/daoc_cms/api_events.php
SharedSecret=REPLACE_WITH_THE_GENERATED_SECRET
BridgePort=2000
```

`CmsApiUrl` must resolve from the **game-server machine**, not just from a browser on another
computer. Use the canonical host name that actually has a DNS record and a valid certificate. Do
not add `www.` unless that exact host exists. A wrong host prevents game-to-Discord guild chat and
live events while Discord-to-game communication may still work.

Copy the downloaded file without renaming it:

```text
<game-server-root>/config/daoc_cms_bridge.conf
```

Restrict read access because the file contains the shared secret.

### 3. Install the C# scripts

Stop the game server and copy the following files to `<game-server-root>/scripts/`:

```text
DAoCCmsBridgeConfig.cs
AldhranBridge.cs
GuildChatBridge.cs
CMSLiveEvents.cs
```

Do not place the files in `scripts/playerclasses/` or another feature subdirectory. Keeping them
directly in `scripts/` also makes the external OpenDAoC builder and later updates predictable.

#### DOL

DOL normally compiles scripts when the server starts:

1. Stop the DOL server.
2. Copy the four C# files to `<DOL-root>/scripts/`.
3. Copy `daoc_cms_bridge.conf` to `<DOL-root>/config/`.
4. Start the DOL server and let its normal script compiler finish.

The command integration uses the public DOL command API when available and retains a compatibility
fallback for older DOL builds. Guild delivery uses DOL's native guild member broadcast API. No
DOL source patch or DOL-specific version of the scripts is required.

#### OpenDAoC with normal script compilation

Use the same layout as DOL:

1. Stop `CoreServer.exe`.
2. Copy the four C# files to `<OpenDAoC-root>/scripts/`.
3. Copy `daoc_cms_bridge.conf` to `<OpenDAoC-root>/config/`.
4. Start `CoreServer.exe` and let OpenDAoC compile the scripts.

#### OpenDAoC builds affected by `Bad IL format`

`tools/Build-OpenDAoCScriptAssembly.ps1` compiles the installed scripts against the assemblies of
the exact OpenDAoC release and writes both files expected by its script cache:

```text
<OpenDAoC-root>/lib/GameServerScripts.dll
<OpenDAoC-root>/lib/GameServerScripts.dll.xml
```

`CoreServer.exe` must be stopped before running the builder because a running server locks the DLL.
For the example layout where both the release folder and builder are on the Administrator desktop,
run PowerShell as Administrator and execute:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Administrator\Desktop\Build-OpenDAoCScriptAssembly.ps1" -ReleasePath "C:\Users\Administrator\Desktop\OpenDAoC\Release"
```

A successful run ends with messages similar to:

```text
Script compilation succeeded.
Pre-compiled assembly written to: ...\lib\GameServerScripts.dll
OpenDAoC script cache written to: ...\lib\GameServerScripts.dll.xml
```

Start `CoreServer.exe` only after the builder has finished. `EnableCompilation=True` may remain set
while the source scripts are unchanged because the cache metadata matches the generated DLL. Run
the builder again after **every** `.cs` script change. Setting `EnableCompilation=False` is therefore
not an architectural requirement; it is only a last-resort setting for an OpenDAoC distribution
that ignores or invalidates a correct cache.

### 4. Publish and configure AldhranConsole

From the `AldhranConsole` project directory, create a Windows release with:

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output publish\win-x64
```

Copy the complete publish directory to its final location. Edit the `appsettings.json` beside the
published executable:

```json
{
  "Console": {
    "ListenUrl": "http://127.0.0.1:5100",
    "SharedSecret": "REPLACE_WITH_THE_SAME_SHARED_SECRET",
    "DbConnection": "Server=127.0.0.1;Port=3306;Database=YOUR_GAME_DATABASE;User ID=YOUR_GAME_DB_USER;Password=YOUR_GAME_DB_PASSWORD;",
    "BridgeHost": "127.0.0.1",
    "BridgePort": 2000,
    "BridgeTimeoutSeconds": 8,
    "ScriptsPath": ""
  }
}
```

The following three values must agree:

```text
CMS game_server_shared_secret
    = AldhranConsole Console:SharedSecret
    = daoc_cms_bridge.conf SharedSecret
```

The bridge compares the complete value, including punctuation; quotes are JSON syntax and are not
part of the secret. Restart AldhranConsole after changing `appsettings.json`. Changing only
`daoc_cms_bridge.conf` requires a game-server restart but no script rebuild.

Start the Console on Windows:

```powershell
.\AldhranConsole.exe
```

Verify the process and the game-server connection separately:

```powershell
Invoke-RestMethod http://127.0.0.1:5100/health
Invoke-RestMethod -Headers @{ "X-Aldhran-Secret" = "REPLACE_WITH_THE_SAME_SHARED_SECRET" } http://127.0.0.1:5100/status
```

`/health` proves that the HTTP service is running. `/status` additionally proves that
AldhranConsole can authenticate to `AldhranBridge.cs` over TCP. See
[`AldhranConsole/README.md`](AldhranConsole/README.md) for Linux service installation and the full
endpoint list.

### 5. Configure Discord guild chat

In **ACP → Bot Settings**:

1. Configure the Discord bot token.
2. Configure the socket host and port; the default bot socket port is `15000`.
3. Set a socket secret. This is the Discord bot's HMAC secret and is separate from the game-server
   shared secret.
4. Set **Bot Script Path** relative to the CMS root. For the bundled script this is
   `assets/js/bot.js`.
5. Enable **Bot Active** and **In-Game Guild Chat Sync**, then save.

Install the Node dependency once from the bot directory:

```powershell
cd C:\path\to\daoc_cms\assets\js
npm install
```

Use the ACP's **Start Bot** button so the CMS supplies the required bootstrap URLs and secret to the
process. If the web server is not allowed to start child processes, run the bot under a service
manager and provide `DAOC_CMS_CONFIG_URL`, `DAOC_CMS_BOOTSTRAP_SECRET` and
`DAOC_CMS_WEBHOOK_URL` as environment variables.

Finally, link a Discord text channel to the in-game guild. The recommended method is to use the
bot's `/createguildchannel` command while the Discord account is linked to an eligible game
account. A successful link stores that channel ID in the compatible guild table. Without this
mapping, the bot reports `No guild linked to this Discord channel`.

The two directions use different paths and should be tested separately:

| Direction | Request path |
| --- | --- |
| Discord → game | Discord bot → `bot_webhook.php` → AldhranConsole `/guildchat` → `AldhranBridge.cs` |
| Game → Discord | `GuildChatBridge.cs` → CMS `api_events.php` → Discord bot socket → linked channel |

### 6. Start order and acceptance test

Use this order after a fresh deployment or update:

1. Start the database and web server.
2. Start DOL or `CoreServer.exe` and wait for scripts to load.
3. Start AldhranConsole.
4. Start the Discord bot from the ACP or service manager.
5. Call AldhranConsole `/health`, then authenticated `/status`.
6. Send a message in the linked Discord guild channel and confirm the `[Discord]` message in game.
7. Send `/gu deployment test` in game and confirm the guild-tagged bot message in Discord.

Useful successful game-server log entries include:

```text
[AldhranBridge] Started on port 2000.
[GuildChatBridge] Guild chat forwarding is active.
```

### 7. Updating

For a source update:

1. Stop the Discord bot, AldhranConsole and game server.
2. Replace the bridge `.cs` files while preserving `config/daoc_cms_bridge.conf`.
3. Re-publish AldhranConsole while preserving the configured production `appsettings.json`.
4. On DOL, start the server and let it compile normally.
5. On an OpenDAoC installation using the external builder, run the builder again before starting
   `CoreServer.exe`.
6. Start the remaining services in the order above and repeat both guild-chat tests.

### 8. Troubleshooting

| Symptom or log message | Cause and action |
| --- | --- |
| `Invalid secret received. Length=X (expected=Y)` | Different secrets are loaded. Compare the three locations above, save the files and restart both AldhranConsole and the game server. Rebuilding scripts is not needed for a config-only change. |
| `No guild linked to this Discord channel` | The Discord channel ID is not mapped to a guild. Run `/createguildchannel` with a linked account and verify the command succeeds. |
| Discord → game works, game → Discord does not | Check the game-server log for the CMS request. Verify that `CmsApiUrl` resolves from the game-server host, has the correct scheme/path and does not use an unavailable `www` host. Then verify the bot socket and channel mapping. |
| `The specified host is unknown` / `Der angegebene Host ist unbekannt` | DNS cannot resolve the host in `CmsApiUrl`. Correct it in the CMS, download a new config, replace the file and restart the game server. |
| `Connection refused` to `127.0.0.1:2000` | `AldhranBridge.cs` is not listening, the game-server scripts did not load, or `BridgeHost`/`BridgePort` is wrong. |
| Builder `Copy-Item` says `GameServerScripts.dll` is in use | `CoreServer.exe` is still running. Stop it, verify no process remains and run the builder again. |
| OpenDAoC reports `Bad IL format` | Stop the server and use `Build-OpenDAoCScriptAssembly.ps1` against that exact release directory. |
| No `[GuildChatBridge]` startup log | The script assembly does not contain the current script. Recompile through DOL or rerun the external OpenDAoC builder, then restart. |

### 9. Network and secret safety

- Keep AldhranConsole on `127.0.0.1` when the CMS runs on the same machine.
- If the CMS is remote, restrict port `5100` by firewall and terminate TLS before the Console.
- Do not expose AldhranBridge TCP port `2000` publicly.
- Do not commit production `appsettings.json`, `daoc_cms_bridge.conf`, bot tokens or database
  passwords.
- Rotate the shared secret in all three locations if it is disclosed.

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0). See [`LICENSE`](LICENSE) for the complete license text.
