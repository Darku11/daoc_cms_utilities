/*
 * DAoC CMS - Guild Chat -> Discord Bridge
 *
 * Lives deliberately in the custom scripts folder (like CMSLiveEvents.cs),
 * NOT in the DOLSharp core project (GameServer\commands\playercommands\guildchat.cs).
 *
 * Reason: SendMessageToGuildMembers() in the core (Guild.cs) doesn't log
 * anything and fires no DOLEvent — there was nothing to hook into from the
 * outside. This script re-registers the &gu / &guild command here in the
 * scripts folder instead. Since the scripts folder loads after the core,
 * this registration overrides the one in the core project (ScriptMgr keeps
 * commands in a dictionary by name; the last-loaded registration wins), so
 * guildchat.cs in the DOLSharp project stays untouched.
 *
 * TO VERIFY AFTER DEPLOYING: check the server console at startup for a
 * message like "duplicate command" / "overriding command" for "&gu"
 * (confirms the override worked), then test /gu in-game and confirm the
 * message still arrives in guild chat as usual AND that api_events.php
 * logs a "guild_chat" event.
 */
using DOL.GS.PacketHandler;
using DOL.Language;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System;

namespace DOL.GS.Commands
{
    [CmdAttribute(
        "&gu",
        new string[] { "&guild" },
        ePrivLevel.Player,
        "Guild Chat command",
        "/gu <text>")]
    public class GuildChatBridgeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly HttpClient _http = new HttpClient();

        // Your site's api_events.php endpoint.
        private const string API_URL = "https://YOUR-SITE.example/api_events.php";

        // Must match "Bridge Secret" under ACP -> General Settings -> Bridge Connection
        // (game_server_bridge_secret).
        private const string BRIDGE_SECRET = "CHANGE_ME_BRIDGE_SECRET";

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
                DisplayMessage(client, LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Guildchat.NoGuild"));
                return;
            }

            if (!client.Player.Guild.HasRank(client.Player, Guild.eRank.GcSpeak))
            {
                DisplayMessage(client, LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Guildchat.NoGuildPermission"));
                return;
            }

            if (IsSpammingCommand(client.Player, "guildchat", 500))
            {
                DisplayMessage(client, LanguageMgr.GetTranslation(client.Account.Language, "GamePlayer.Spamming.Say"));
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
