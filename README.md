# DAoC CMS Utilities

Server-side and supporting components for [DAoC CMS](https://github.com/Darku11/daoc_cms) — the pieces that don't live inside the CMS web application itself, because they run on or alongside the game server instead.

None of this is required for a basic DAoC CMS installation. Each component only matters if you're using the CMS feature it powers. See `daoc_cms`'s [`setup/steps/bridges.php`](https://github.com/Darku11/daoc_cms/blob/main/setup/steps/bridges.php) (the "Bridges" step of the installer) for how these pieces fit together end to end.

## Components

### AldhranBridge.cs
The in-game console bridge. Runs as a script inside your DOL or OpenDAoC server's `scripts/` folder and is the only thing that actually touches players, items, and the world on behalf of the CMS — status, kick, privlevel, teleport, item delivery, guild chat relay, restart, and more. Listens on TCP port 2000 for commands forwarded by AldhranConsole.

Runs unmodified on both **DOL** and **OpenDAoC** — the two forks' diverging APIs are resolved at load time through a small reflection-based compatibility shim (`ServerCoreCompat`) inside the file.

### AldhranConsole/
The HTTP bridge between the DAoC CMS website and AldhranBridge.cs. A standalone ASP.NET Core (`net10.0`) minimal-API service — not a scripts-folder drop-in, it runs as its own process anywhere it can reach both the CMS and the game server. The CMS talks to it over HTTP (port 5100, `X-Aldhran-Secret` header); it forwards admin/console actions to AldhranBridge.cs over TCP and talks to the game database directly for the itemshop, item-autocomplete, zone and World Forge endpoints.

It doesn't reference any DOL/OpenDAoC server assemblies. Core-specific live actions are handled by AldhranBridge's compatibility layer, while the Console uses database tables shared by the supported DOL and OpenDAoC schemas. The same Console build therefore runs with **either** server implementation.

Copy `appsettings.json` and fill in `SharedSecret` and `DbConnection` before running it — the shipped file only contains placeholders. `SharedSecret` must match `game_server_shared_secret` in the CMS and the `BRIDGE_SECRET` constant in `AldhranBridge.cs`.

See [`AldhranConsole/README.md`](AldhranConsole/README.md) for configuration, build, publishing, endpoint and security instructions.

### CMSLiveEvents.cs
Pushes PvP kill and keep-capture events from the game server to the CMS's live event feed (`api_events.php`). Drop-in script for the `scripts/` folder, runs unmodified on DOL and OpenDAoC.

### GuildChatBridge.cs
Relays in-game guild chat to Discord by re-registering the `&gu` command. Drop-in script for the `scripts/` folder, runs unmodified on DOL and OpenDAoC.

### database.sql
The DAoC CMS database schema (CMS-side tables, not the game server database).

### TerrainService-RC1-win-x64.zip
Supporting terrain/map data service used by some admin tools. Only needed if a tool you're running depends on it.

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0). See [`LICENSE`](LICENSE) for the complete license text.
