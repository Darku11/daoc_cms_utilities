/* SPDX-License-Identifier: GPL-3.0-only */
/*
 * DAoC CMS - Guild Chat -> Discord Bridge
 *
 * Lives deliberately in the custom scripts folder (like CMSLiveEvents.cs),
 * NOT in the DOL/OpenDAoC core project (GameServer\commands\playercommands\guildchat.cs).
 *
 * Reason: SendMessageToGuildMembers() in the core (Guild.cs) doesn't log
 * anything and fires no post-send DOLEvent. OpenDAoC loads script commands
 * before the built-in core commands, so this script claims /gu directly. The
 * installer below remains as a compatibility fallback for DOL builds that use
 * a different assembly order. guildchat.cs in the server core stays untouched.
 *
 * TO VERIFY AFTER DEPLOYING: test /gu in-game and confirm the message still
 * arrives in guild chat as usual AND that api_events.php logs a "guild_chat"
 * event. OpenDAoC may report that its built-in &gu command was suppressed;
 * that is expected because the script command was registered first.
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
    internal static class GuildChatBridgeLog
    {
        public static void Info(string message) { Write("Info", message, false); }
        public static void Warn(string message) { Write("Warn", message, true); }
        public static void Error(string message) { Write("Error", message, true); }

        private static void Write(string level, string message, bool error)
        {
            string formatted = "[GuildChatBridge] " + message;

            try
            {
                PropertyInfo instanceProperty = typeof(GameServer).GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static);
                object server = instanceProperty?.GetValue(null);
                PropertyInfo logProperty = server?.GetType().GetProperty(
                    "Log",
                    BindingFlags.Public | BindingFlags.Instance);
                object logger = logProperty?.GetValue(server);

                if (logger != null)
                {
                    foreach (MethodInfo method in logger.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name == level
                            && parameters.Length == 1
                            && parameters[0].ParameterType.IsAssignableFrom(typeof(string)))
                        {
                            method.Invoke(logger, new object[] { formatted });
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to the server console.
            }

            if (error)
                Console.Error.WriteLine(formatted);
            else
                Console.WriteLine(formatted);
        }
    }

    public static class GuildChatBridgeInstaller
    {
        private static readonly object _sync = new object();
        private static ScriptMgr.GameCommand _guildCommand;
        private static ScriptMgr.GameCommand _guildAliasCommand;
        private static ICommandHandler _originalGuildHandler;
        private static ICommandHandler _originalGuildAliasHandler;
        private static GuildChatBridgeCommandHandler _bridgeHandler;
        private static bool _installed;

        private static ScriptMgr.GameCommand FindCommand(string name)
        {
            MethodInfo getCommand = typeof(ScriptMgr).GetMethod(
                "GetCommand",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (getCommand != null)
                return getCommand.Invoke(null, new object[] { name }) as ScriptMgr.GameCommand;

            // Compatibility fallback for older DOL builds without GetCommand.
            FieldInfo registryField = typeof(ScriptMgr).GetField(
                "m_gameCommands", BindingFlags.NonPublic | BindingFlags.Static);
            var registry = registryField?.GetValue(null)
                as Dictionary<string, ScriptMgr.GameCommand>;

            return registry != null && registry.TryGetValue(name, out ScriptMgr.GameCommand command)
                ? command
                : null;
        }

        [ScriptLoadedEvent]
        public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            lock (_sync)
            {
                if (_installed)
                    return;

                string configError;
                if (!DAoCCmsBridgeConfig.TryLoad(out configError))
                {
                    GuildChatBridgeLog.Error(configError);
                    return;
                }

                // Respect DISABLED_COMMANDS and custom cores: do not silently
                // enable guild chat when the server did not register /gu.
                _guildCommand = FindCommand("&gu");
                if (_guildCommand == null || _guildCommand.m_cmdHandler == null)
                {
                    GuildChatBridgeLog.Warn("The &gu command is unavailable; the bridge was not installed.");
                    _guildCommand = null;
                    return;
                }

                _bridgeHandler = new GuildChatBridgeCommandHandler();
                _originalGuildHandler = _guildCommand.m_cmdHandler;

                // OpenDAoC loads commands from the script assembly first. In
                // that case the CmdAttribute below already installed this
                // handler and there is nothing left to replace.
                if (_originalGuildHandler is GuildChatBridgeCommandHandler installedHandler)
                {
                    _bridgeHandler = installedHandler;
                    _originalGuildHandler = null;
                    _installed = true;
                    GuildChatBridgeLog.Info(
                        "Guild chat forwarding is active. Configuration=" +
                        DAoCCmsBridgeConfig.ConfigPath + ".");
                    return;
                }

                _guildCommand.m_cmdHandler = _bridgeHandler;

                ScriptMgr.GameCommand alias = FindCommand("&guild");
                if (alias != null && !ReferenceEquals(alias, _guildCommand))
                {
                    _guildAliasCommand = alias;
                    _originalGuildAliasHandler = alias.m_cmdHandler;
                    alias.m_cmdHandler = _bridgeHandler;
                }

                _installed = true;
                GuildChatBridgeLog.Info(
                    "Guild chat forwarding is active. Configuration=" +
                    DAoCCmsBridgeConfig.ConfigPath + ".");
            }
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            lock (_sync)
            {
                if (!_installed)
                    return;

                RestoreHandler(_guildCommand, _originalGuildHandler);
                RestoreHandler(_guildAliasCommand, _originalGuildAliasHandler);

                _guildCommand = null;
                _guildAliasCommand = null;
                _originalGuildHandler = null;
                _originalGuildAliasHandler = null;
                _bridgeHandler = null;
                _installed = false;
            }
        }

        private static void RestoreHandler(ScriptMgr.GameCommand command, ICommandHandler originalHandler)
        {
            if (command != null
                && originalHandler != null
                && ReferenceEquals(command.m_cmdHandler, _bridgeHandler))
                command.m_cmdHandler = originalHandler;
        }
    }

    [CmdAttribute(
        "&gu",
        new string[] { "&guild" },
        ePrivLevel.Player,
        "Guild Chat command",
        "/gu <text>")]
    public class GuildChatBridgeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

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
            string apiUrl = DAoCCmsBridgeConfig.CmsApiUrl;
            string sharedSecret = DAoCCmsBridgeConfig.SharedSecret;

            Task.Run(async () =>
            {
                try
                {
                    using (var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("secret", sharedSecret),
                        new KeyValuePair<string, string>("type", "guild_chat"),
                        new KeyValuePair<string, string>("guild", guildName),
                        new KeyValuePair<string, string>("player", playerName),
                        new KeyValuePair<string, string>("message", message)
                    }))
                    using (HttpResponseMessage response = await _http.PostAsync(apiUrl, content).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            GuildChatBridgeLog.Error(
                                "CMS returned HTTP " + (int)response.StatusCode + ": " + Shorten(responseBody));
                            return;
                        }

                        if (responseBody.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            GuildChatBridgeLog.Error("CMS rejected the guild-chat event: " + Shorten(responseBody));
                            return;
                        }

                        GuildChatBridgeLog.Info(
                            "Forwarded guild chat from '" + playerName + "' in guild '" + guildName + "'.");
                    }
                }
                catch (Exception ex)
                {
                    GuildChatBridgeLog.Error("CMS request failed: " + ex.Message);
                }
            });
        }

        private static string Shorten(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "empty response" : value.Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500) + "...";
        }

        public void OnCommand(GameClient client, string[] args)
        {
            // ── 1:1 identical to the original core logic from guildchat.cs,
            // so nothing changes about the player experience.
            if (client?.Player == null)
                return;

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

            string rawText = args != null && args.Length > 1
                ? string.Join(" ", args, 1, args.Length - 1)
                : string.Empty;
            string message = "[Guild] " + client.Player.Name + ": \"" + rawText + "\"";
            client.Player.Guild.SendMessageToGuildMembers(message, eChatType.CT_Guild, eChatLoc.CL_ChatWindow);

            // ── Forward to the CMS/Discord bridge.
            GuildChatBridgeLog.Info(
                "Captured guild chat from '" + client.Player.Name + "' in guild '" + client.Player.Guild.Name + "'.");
            SendGuildChatToCms(client.Player.Guild.Name, client.Player.Name, rawText);
        }
    }
}
