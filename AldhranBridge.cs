using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DOL.GS;
using DOL.GS.Geometry;
using DOL.GS.PacketHandler;
using DOL.Database;
using DOL.Events;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Aldhran Console Bridge.
    /// Listens on BRIDGE_PORT and serves every action the AldhranConsole (the
    /// ASP.NET service, "program.cs") sends: status, kick, privlevel, gmmode,
    /// teleport, giveitem, setstats, broadcast, restart, raw, heal, revive,
    /// freeze, mute, guildchat.
    ///
    /// This script is the one and only in-game delivery path for admin/console
    /// actions (including item purchases from the CMS itemshop). Do not run an
    /// older itemshop.cs / WebShopService / WebShopPoller / WebShopDispatcher
    /// alongside it — two systems answering the same purpose will conflict.
    /// </summary>
    public class AldhranBridge
    {
        private static TcpListener _listener;
        private static bool _isRunning;

        // Must match "Console:BridgeSecret" in the AldhranConsole's appsettings.json exactly.
        private const string BRIDGE_SECRET = "CHANGE_ME_BRIDGE_SECRET";
        private const int BRIDGE_PORT = 2000;

        // Commands that must never run via /raw, even for a SuperAdmin
        // (in addition to any blocklist on the AldhranConsole side — defense in depth).
        private static readonly HashSet<string> BLOCKED_RAW_COMMANDS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shutdown", "quit"
        };

        // Remembers the original run speed of frozen players (Name -> MaxSpeedBase
        // before the freeze), so Unfreeze restores the exact previous value instead
        // of guessing a hardcoded base.
        private static readonly Dictionary<string, short> _frozenSpeed = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [ScriptLoadedEvent]
        public static void OnScriptCompiled(DOLEvent e, object sender, EventArgs args)
        {
            if (_isRunning) return;
            _isRunning = true;
            _listener = new TcpListener(IPAddress.Any, BRIDGE_PORT);
            _listener.Start();
            log.Info($"[AldhranBridge] Started on port {BRIDGE_PORT}.");
            Task.Run(() => ListenLoop());
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            _isRunning = false;
            _listener?.Stop();
            log.Info("[AldhranBridge] Stopped.");
        }

        private static async Task ListenLoop()
        {
            while (_isRunning)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex)
                {
                    if (_isRunning) log.Warn("[AldhranBridge] Accept error: " + ex.Message);
                    continue;
                }

                // Handle each connection in parallel so one slow/hanging client
                // doesn't block every other request.
                _ = HandleClientAsync(client);
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;

                    using (var stream = client.GetStream())
                    using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true))
                    {
                        // The console writes two lines (secret, then JSON) via WriteLineAsync.
                        // ReadLineAsync is more robust against TCP fragmentation than a single
                        // ReadAsync call on raw bytes.
                        string secret = await reader.ReadLineAsync();
                        string json = await reader.ReadLineAsync();

                        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(json))
                            return;

                        if (secret != BRIDGE_SECRET)
                        {
                            string preview = secret.Length <= 6
                                ? new string('*', secret.Length)
                                : secret.Substring(0, 3) + "..." + secret.Substring(secret.Length - 3);
                            log.Warn($"[AldhranBridge] Invalid secret received. " +
                                     $"Length={secret.Length} (expected={BRIDGE_SECRET.Length}), preview='{preview}'");
                            return;
                        }

                        string response;
                        try
                        {
                            JObject cmd = JObject.Parse(json);
                            string action = cmd.Value<string>("action");
                            response = ProcessAction(action, cmd);
                        }
                        catch (Exception ex)
                        {
                            log.Error("[AldhranBridge] Processing error: " + ex.Message);
                            response = JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
                        }

                        byte[] outBytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(outBytes, 0, outBytes.Length);
                        await stream.FlushAsync();

                        // Signal "done sending" (FIN) first, so the client's read loop
                        // (which reads until EOF) ends cleanly instead of an abrupt
                        // Dispose() occasionally triggering an RST.
                        try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] Client error: " + ex.Message);
            }
        }

        private static string ProcessAction(string action, JObject cmd)
        {
            switch (action)
            {
                case "status":
                    return Handle_Status();

                case "kick":
                    return Handle_Kick(cmd.Value<string>("name"), cmd.Value<string>("reason"));

                case "privlevel":
                    return Handle_PrivLevel(cmd.Value<string>("name"), cmd.Value<int>("level"));

                case "teleport":
                    return Handle_Teleport(cmd.Value<string>("name"), cmd.Value<int>("x"), cmd.Value<int>("y"), cmd.Value<int>("z"), cmd.Value<int>("region"));

                case "giveitem":
                    return Handle_GiveItem(cmd.Value<string>("name"), cmd.Value<string>("item_id"), cmd.Value<int>("count"));

                case "guildchat":
                    return Handle_GuildChat(cmd.Value<string>("guild"), cmd.Value<string>("sender"), cmd.Value<string>("message"));

                case "setstats":
                    return Handle_SetStats(cmd.Value<string>("name"), cmd.Value<string>("stat"), cmd.Value<int>("value"));

                case "broadcast":
                    return Handle_Broadcast(cmd.Value<string>("message"), cmd.Value<string>("sender"));

                case "restart":
                    return Handle_Restart(cmd.Value<int>("delay_minutes"), cmd.Value<string>("announcement"), cmd.Value<string>("sender"));

                case "raw":
                    return Handle_RawCommand(cmd.Value<string>("executor"), cmd.Value<string>("command"));

                case "heal":
                    return Handle_Heal(cmd.Value<string>("name"));

                case "revive":
                    return Handle_Revive(cmd.Value<string>("name"));

                case "freeze":
                    return Handle_Freeze(cmd.Value<string>("name"), cmd.Value<bool>("on"));

                case "mute":
                    return Handle_Mute(cmd.Value<string>("name"), cmd.Value<bool>("on"));

                default:
                    return JsonConvert.SerializeObject(new { ok = false, error = "Unknown action: " + action });
            }
        }

        // ── status ────────────────────────────────────────────────
        private static string Handle_Status()
        {
            var players = new List<object>();

            foreach (GameClient gameClient in WorldMgr.GetAllClients())
            {
                if (gameClient.Player != null)
                {
                    GamePlayer p = gameClient.Player;
                    players.Add(new
                    {
                        Name = p.Name,
                        Level = p.Level,
                        AccountName = gameClient.Account?.Name,
                        // eCharacterClass is a byte-backed enum — Enum.IsDefined/cast need an
                        // explicit (byte), and IsDefined guards against invalid/unknown IDs
                        // (e.g. 0 on a freshly created character with no class chosen yet).
                        Class = (p.CharacterClass != null && Enum.IsDefined(typeof(eCharacterClass), (byte)p.CharacterClass.ID))
                            ? ((eCharacterClass)(byte)p.CharacterClass.ID).ToString()
                            : "Unknown",
                        Region = p.CurrentRegion?.Description,
                        PrivLevel = gameClient.Account?.PrivLevel ?? 0
                    });
                }
            }

            return JsonConvert.SerializeObject(new
            {
                ok = true,
                server_online = true,
                players = players,
                server_time = DateTime.UtcNow.ToString("o")
            });
        }

        // ── kick ──────────────────────────────────────────────────
        private static string Handle_Kick(string name, string reason)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Out.SendMessage(reason ?? "You have been kicked from the server.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                client.Player.SaveIntoDatabase();
                GameServer.Instance.Disconnect(client);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── privlevel ─────────────────────────────────────────────
        private static string Handle_PrivLevel(string name, int level)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Account.PrivLevel = (uint)level;
                GameServer.Database.SaveObject(client.Account);
                client.Out.SendMessage($"Your privilege level has been set to {level}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── teleport ──────────────────────────────────────────────
        private static string Handle_Teleport(string name, int x, int y, int z, int region)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                var teleportPosition = Position.Create(
                    (ushort)region, x, y, z, client.Player.Orientation.InHeading);
                client.Player.MoveTo(teleportPosition);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── giveitem ──────────────────────────────────────────────
        private static string Handle_GiveItem(string buyerName, string itemId, int count)
        {
            GamePlayer player = null;
            foreach (GameClient gameClient in WorldMgr.GetAllClients())
            {
                if (gameClient.Player != null && gameClient.Player.Name.Equals(buyerName, StringComparison.OrdinalIgnoreCase))
                {
                    player = gameClient.Player;
                    break;
                }
            }

            if (player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                ItemTemplate template = GameServer.Database.FindObjectByKey<ItemTemplate>(itemId);
                if (template == null)
                    return JsonConvert.SerializeObject(new { ok = false, error = "Item not found." });

                GameInventoryItem item = new GameInventoryItem(template);
                item.Count = count;

                if (player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item))
                {
                    player.Out.SendInventoryItemsUpdate(new InventoryItem[] { item });
                    player.UpdateEncumberance();
                    player.SaveIntoDatabase();
                    player.Out.SendMessage($"You have received {template.Name} x{count}!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    return JsonConvert.SerializeObject(new { ok = true });
                }

                return JsonConvert.SerializeObject(new { ok = false, error = "Inventory is full." });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] giveitem error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── setstats ──────────────────────────────────────────────
        private static string Handle_SetStats(string name, string stat, int value)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;
                string statKey = stat.ToLowerInvariant();

                switch (statKey)
                {
                    case "hp":
                        player.Health = value;
                        player.Out.SendCharStatsUpdate();
                        player.UpdatePlayerStatus();
                        break;

                    case "mana":
                        player.Mana = value;
                        player.Out.SendCharStatsUpdate();
                        break;

                    case "endurance":
                        player.Endurance = value;
                        player.Out.SendCharStatsUpdate();
                        break;

                    case "level":
                        player.Level = (byte)value;
                        player.SaveIntoDatabase();
                        player.Out.SendUpdatePlayer();
                        player.Out.SendCharStatsUpdate();
                        break;

                    case "xp":
                        // GainExperience(long, ...) — an absolute "gain" relative to 0, since
                        // there is no direct setter for the total XP standing.
                        player.GainExperience(GameLiving.eXPSource.Other, value, 0, 0, 0, false, false, false);
                        break;

                    case "gold":
                        {
                            long deltaCopper = (value - player.Gold) * 10_000L;
                            if (deltaCopper > 0) player.AddMoney(deltaCopper);
                            else if (deltaCopper < 0) player.RemoveMoney(-deltaCopper);
                            player.Out.SendUpdateMoney();
                            break;
                        }

                    case "platinum":
                        {
                            long deltaCopper = (value - player.Platinum) * 10_000_000L;
                            if (deltaCopper > 0) player.AddMoney(deltaCopper);
                            else if (deltaCopper < 0) player.RemoveMoney(-deltaCopper);
                            player.Out.SendUpdateMoney();
                            break;
                        }

                    case "silver":
                        {
                            long deltaCopper = (value - player.Silver) * 100L;
                            if (deltaCopper > 0) player.AddMoney(deltaCopper);
                            else if (deltaCopper < 0) player.RemoveMoney(-deltaCopper);
                            player.Out.SendUpdateMoney();
                            break;
                        }

                    case "copper":
                        {
                            long deltaCopper = (value - player.Copper);
                            if (deltaCopper > 0) player.AddMoney(deltaCopper);
                            else if (deltaCopper < 0) player.RemoveMoney(-deltaCopper);
                            player.Out.SendUpdateMoney();
                            break;
                        }

                    default:
                        return JsonConvert.SerializeObject(new { ok = false, error = "Unknown stat: " + stat });
                }

                player.SaveIntoDatabase();
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] setstats error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── broadcast ─────────────────────────────────────────────
        private static string Handle_Broadcast(string message, string sender)
        {
            try
            {
                foreach (GameClient gameClient in WorldMgr.GetAllClients())
                {
                    if (gameClient.Player != null)
                        gameClient.Out.SendMessage($"[{sender}] {message}", eChatType.CT_Broadcast, eChatLoc.CL_SystemWindow);
                }
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── guildchat ─────────────────────────────────────────────
        private static string Handle_GuildChat(string guildName, string sender, string message)
        {
            if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(message))
                return JsonConvert.SerializeObject(new { ok = false, error = "Missing parameters." });

            try
            {
                int count = 0;
                string formattedMessage = $"[Discord] {sender}: {message}";

                foreach (GameClient gameClient in WorldMgr.GetAllClients())
                {
                    if (gameClient.Player != null && gameClient.Player.Guild != null)
                    {
                        if (gameClient.Player.Guild.Name.Equals(guildName, StringComparison.OrdinalIgnoreCase))
                        {
                            gameClient.Out.SendMessage(formattedMessage, eChatType.CT_Guild, eChatLoc.CL_ChatWindow);
                            count++;
                        }
                    }
                }
                return JsonConvert.SerializeObject(new { ok = true, recipients = count });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] guildchat error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── restart ───────────────────────────────────────────────
        // Optional external hook for Discord/AI announcements on server restarts.
        // AldhranBridge is a purely static DOL script class with no DI container,
        // so it does not instantiate any Discord/AI service itself. A separate
        // script that knows your real service instances can set this hook on its
        // own [ScriptLoadedEvent]:
        //
        //   AldhranBridge.RestartAnnouncementHook = async (announcement, delayMinutes) =>
        //   {
        //       // ... call your own Discord/AI services here ...
        //   };
        //
        // Left unset (null), nothing happens — restart and the in-game broadcast
        // work independently either way. The hook runs fire-and-forget so a
        // hanging Discord/AI call never delays or blocks the actual shutdown.
        public static Func<string, int, Task> RestartAnnouncementHook = null;

        private static string Handle_Restart(int delayMinutes, string announcement, string sender)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(announcement))
                    Handle_Broadcast(announcement, sender);

                if (RestartAnnouncementHook != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RestartAnnouncementHook(announcement ?? "", delayMinutes);
                        }
                        catch (Exception hookEx)
                        {
                            log.Warn("[AldhranBridge] RestartAnnouncementHook error: " + hookEx.Message);
                        }
                    });
                }

                Task.Delay(delayMinutes * 60 * 1000).ContinueWith(_ =>
                {
                    GameServer.Instance.Stop();
                });

                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── raw ───────────────────────────────────────────────────
        // The built-in PrivLevel check of ScriptMgr.HandleCommand is intentionally
        // NOT run here (we call m_cmdHandler.OnCommand directly), because the PHP
        // side already restricts this to SuperAdmins. BLOCKED_RAW_COMMANDS is a
        // second, independent layer of protection.
        private static string Handle_RawCommand(string executorName, string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return JsonConvert.SerializeObject(new { ok = false, error = "No command given." });

            string trimmed = commandLine.Trim();
            if (trimmed.StartsWith("/"))
                trimmed = trimmed.Substring(1);

            string[] pars = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (pars.Length == 0)
                return JsonConvert.SerializeObject(new { ok = false, error = "No command given." });

            string cmdName = pars[0];

            if (BLOCKED_RAW_COMMANDS.Contains(cmdName))
                return JsonConvert.SerializeObject(new { ok = false, error = $"Command '{cmdName}' is blocked." });

            try
            {
                // Prefer an executor by name; otherwise fall back to the first
                // online GM/admin (PrivLevel >= 2), since some command handlers
                // require a GameClient with sufficient PrivLevel.
                GameClient executorClient = null;

                if (!string.IsNullOrWhiteSpace(executorName))
                    executorClient = WorldMgr.GetClientByPlayerName(executorName, true, false);

                if (executorClient == null)
                {
                    foreach (GameClient gc in WorldMgr.GetAllClients())
                    {
                        if (gc.Player != null && gc.Account != null && gc.Account.PrivLevel >= 2)
                        {
                            executorClient = gc;
                            break;
                        }
                    }
                }

                if (executorClient == null || executorClient.Player == null)
                    return JsonConvert.SerializeObject(new { ok = false, error = "No suitable executor (online GM/admin) found." });

                var cmdEntry = ScriptMgr.GuessCommand("/" + cmdName);
                if (cmdEntry == null)
                    return JsonConvert.SerializeObject(new { ok = false, error = $"Unknown command: {cmdName}" });

                pars[0] = "/" + cmdName;

                cmdEntry.m_cmdHandler.OnCommand(executorClient, pars);

                log.Warn($"[AldhranBridge] RAW COMMAND executed by '{executorClient.Player.Name}': {commandLine}");
                return JsonConvert.SerializeObject(new { ok = true, result = $"Command '{cmdName}' executed (executor: {executorClient.Player.Name})." });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] raw error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── heal ──────────────────────────────────────────────────
        private static string Handle_Heal(string name)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;
                player.Health = player.MaxHealth;
                player.Mana = player.MaxMana;
                player.Endurance = player.MaxEndurance;
                player.Out.SendCharStatsUpdate();
                player.UpdatePlayerStatus();
                player.Out.SendMessage("You have been fully healed by an admin.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── revive ────────────────────────────────────────────────
        private static string Handle_Revive(string name)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;
                player.Release(GamePlayer.eReleaseType.Normal, true);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── freeze / unfreeze ─────────────────────────────────────
        // Known limitation: if the server restarts while a player is frozen, this
        // in-memory dictionary is lost and the player stays at MaxSpeedBase = 0 in
        // the DB (since MaxSpeedBase is persisted). Acceptable risk for an admin
        // tool; can be made more robust with a purely client-side movement lock
        // that isn't persisted, if needed.
        private static string Handle_Freeze(string name, bool on)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;

                if (on)
                {
                    _frozenSpeed[player.Name] = player.MaxSpeedBase;
                    player.MaxSpeedBase = 0;
                    player.Out.SendMessage("You have been frozen by an admin.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }
                else
                {
                    short restoredSpeed = _frozenSpeed.TryGetValue(player.Name, out short savedSpeed)
                        ? savedSpeed
                        : (short)191; // Fallback: DAoC standard base speed, if no value was saved.

                    player.MaxSpeedBase = restoredSpeed;
                    _frozenSpeed.Remove(player.Name);
                    player.Out.SendMessage("You have been unfrozen.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                player.SaveIntoDatabase();
                player.Out.SendUpdateMaxSpeed();
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] freeze error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── mute / unmute ─────────────────────────────────────────
        private static string Handle_Mute(string name, bool on)
        {
            GameClient client = WorldMgr.GetClientByPlayerName(name, true, false);
            if (client == null || client.Player == null)
                return JsonConvert.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Player.IsMuted = on;
                client.Player.Out.SendMessage(
                    on ? "You have been muted by an admin." : "You have been unmuted.",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return JsonConvert.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] mute error: " + ex.Message);
                return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
        }
    }
}
