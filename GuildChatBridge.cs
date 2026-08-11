/*
 * DAoC CMS - Guild Chat -> Discord Bridge
 *
 * Lives deliberately in the custom scripts folder (like CMSLiveEvents.cs),
 * NOT in the DOL/OpenDAoC core project (GameServer\commands\playercommands\guildchat.cs).
 *
 * Reason: SendMessageToGuildMembers() in the core (Guild.cs) doesn't log
 * anything and fires no post-send DOLEvent. Registering a second CmdAttribute
 * is not sufficient either: both DOLSharp and OpenDAoC suppress duplicate
 * commands. After all commands have loaded, this script therefore replaces
 * the two guild-chat dictionary entries and restores them when scripts unload.
 * guildchat.cs in the server core stays untouched.
 *
 * TO VERIFY AFTER DEPLOYING: test /gu in-game and confirm the message still
 * arrives in guild chat as usual AND that api_events.php logs a "guild_chat"
 * event. There should be no duplicate-command warning for this script.
 */
using DOL.Events;
using DOL.GS.PacketHandler;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System;

namespace DOL.GS.Commands
{
    public static class GuildChatBridgeInstaller
    {
        private static readonly object _sync = new object();
        private static Dictionary<string, ScriptMgr.GameCommand> _commands;
        private static ScriptMgr.GameCommand _originalGuildCommand;
        private static ScriptMgr.GameCommand _originalGuildAlias;
        private static bool _hadGuildAlias;
        private static bool _installed;

        private static Dictionary<string, ScriptMgr.GameCommand> GetCommandDictionary()
        {
            FieldInfo field = typeof(ScriptMgr).GetField(
                "m_gameCommands",
                BindingFlags.NonPublic | BindingFlags.Static);

            return field == null
                ? null
                : field.GetValue(null) as Dictionary<string, ScriptMgr.GameCommand>;
        }

        [ScriptLoadedEvent]
        public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            lock (_sync)
            {
                if (_installed)
                    return;

                _commands = GetCommandDictionary();
                if (_commands == null)
                    return;

                // Respect DISABLED_COMMANDS and custom cores: do not silently
                // enable guild chat when the server did not register /gu.
                if (!_commands.TryGetValue("&gu", out _originalGuildCommand)
                    || _originalGuildCommand == null)
                {
                    _commands = null;
                    return;
                }
                _hadGuildAlias = _commands.TryGetValue("&guild", out _originalGuildAlias);

                ScriptMgr.GameCommand bridgeCommand = new ScriptMgr.GameCommand();
                bridgeCommand.Usage = _originalGuildCommand.Usage;
                bridgeCommand.m_cmd = _originalGuildCommand.m_cmd;
                bridgeCommand.m_lvl = _originalGuildCommand.m_lvl;
                bridgeCommand.m_desc = _originalGuildCommand.m_desc;
                bridgeCommand.m_cmdHandler = new GuildChatBridgeCommandHandler();

                _commands["&gu"] = bridgeCommand;
                if (_hadGuildAlias)
                    _commands["&guild"] = bridgeCommand;
                _installed = true;
            }
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            lock (_sync)
            {
                if (!_installed || _commands == null)
                    return;

                RestoreCommand("&gu", _originalGuildCommand);
                if (_hadGuildAlias)
                    RestoreCommand("&guild", _originalGuildAlias);

                _commands = null;
                _originalGuildCommand = null;
                _originalGuildAlias = null;
                _hadGuildAlias = false;
                _installed = false;
            }
        }

        private static void RestoreCommand(string name, ScriptMgr.GameCommand command)
        {
            if (command == null)
                _commands.Remove(name);
            else
                _commands[name] = command;
        }
    }

    public class GuildChatBridgeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly HttpClient _http = new HttpClient();

        // Your site's api_events.php endpoint.
        private const string API_URL = "https://YOUR-SITE.example/api_events.php";

        // Must match "Bridge Secret" under ACP -> General Settings -> Bridge Connection
        // (game_server_bridge_secret).
        private const string BRIDGE_SECRET = "CHANGE_ME_BRIDGE_SECRET";

        // DOL accepts a params object[] while current OpenDAoC exposes a
        // dedicated two-string overload. Resolve either signature and fall
        // back to the built-in English guild-chat messages if translation
        // lookup is unavailable.
        private static string Text(GameClient client, string key, string fallback)
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type languageManager = assembly.GetType("DOL.Language.LanguageMgr", false);
                    if (languageManager == null)
                        continue;

                    string language = client?.Account?.Language ?? string.Empty;
                    MethodInfo getTranslation = languageManager.GetMethod(
                        "GetTranslation",
                        new[] { typeof(string), typeof(string) });
                    object[] invocationArgs = new object[] { language, key };

                    if (getTranslation == null)
                    {
                        getTranslation = languageManager.GetMethod(
                            "GetTranslation",
                            new[] { typeof(string), typeof(string), typeof(object[]) });
                        invocationArgs = new object[] { language, key, new object[0] };
                    }

                    if (getTranslation == null)
                        break;

                    string translated = getTranslation.Invoke(null, invocationArgs) as string;
                    return string.IsNullOrWhiteSpace(translated) ? fallback : translated;
                }
            }
            catch
            {
                // A translation failure must never break the chat command.
            }

            return fallback;
        }

        private static void SendGuildChatToCms(string guildName, string playerName, string message)
        {
            Task.Run(async () =>
            {
                try
                {
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("secret", BRIDGE_SECRET),
                        new KeyValuePair<string, string>("type", "guild_chat"),
                        new KeyValuePair<string, string>("guild", guildName),
                        new KeyValuePair<string, string>("player", playerName),
                        new KeyValuePair<string, string>("message", message)
                    });

                    await _http.PostAsync(API_URL, content);
                }
                catch
                {
                    // Intentionally empty so a failing web request never crashes the game server.
                }
            });
        }

        public void OnCommand(GameClient client, string[] args)
        {
            // ── 1:1 identical to the original core logic from guildchat.cs,
            // so nothing changes about the player experience.
            if (client.Player.Guild == null)
            {
                DisplayMessage(client, Text(client, "Scripts.Players.Guildchat.NoGuild", "You don't belong to a player guild."));
                return;
            }

            if (!client.Player.Guild.HasRank(client.Player, Guild.eRank.GcSpeak))
            {
                DisplayMessage(client, Text(client, "Scripts.Players.Guildchat.NoGuildPermission", "You don't have permission to speak on the guild channel."));
                return;
            }

            if (IsSpammingCommand(client.Player, "guildchat", 500))
            {
                DisplayMessage(client, Text(client, "GamePlayer.Spamming.Say", "Slow down! Think before you say each word!"));
                return;
            }

            string rawText = string.Join(" ", args, 1, args.Length - 1);
            string message = "[Guild] " + client.Player.Name + ": \"" + rawText + "\"";
            client.Player.Guild.SendMessageToGuildMembers(message, eChatType.CT_Guild, eChatLoc.CL_ChatWindow);

            // ── Forward to the CMS/Discord bridge.
            SendGuildChatToCms(client.Player.Guild.Name, client.Player.Name, rawText);
        }
    }
}
