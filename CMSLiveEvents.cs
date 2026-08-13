/* SPDX-License-Identifier: GPL-3.0-only */
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using DOL.Events;
using DOL.GS;
using DOL.GS.Keeps;

namespace DOL.GS.Scripts
{
    public class CMSLiveEvents
    {
        private static readonly HttpClient _http = new HttpClient();

        // Your site's api_events.php endpoint.
        private const string API_URL = "https://YOUR-SITE.example/api_events.php";

        // Must match "Shared Secret" under ACP -> General Settings -> Bridge Connection
        // (game_server_shared_secret).
        private const string BRIDGE_SECRET = "CHANGE_ME_BRIDGE_SECRET";

        [ScriptLoadedEvent]
        public static void OnScriptCompiled(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.AddHandler(GameLivingEvent.Dying, OnPlayerDying);
            GameEventMgr.AddHandler(KeepEvent.KeepTaken, OnKeepCaptured);
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnPlayerDying);
            GameEventMgr.RemoveHandler(KeepEvent.KeepTaken, OnKeepCaptured);
        }

        private static void OnPlayerDying(DOLEvent e, object sender, EventArgs args)
        {
            var victim = sender as GamePlayer;
            var dyingArgs = args as DyingEventArgs;

            if (victim == null || dyingArgs == null) return;

            var killer = dyingArgs.Killer as GamePlayer;
            if (killer == null) return; // Only PvP kills count.

            string msg = $"{killer.Name} killed {victim.Name}!";
            SendEventAsync("kill", msg);
        }

        private static void OnKeepCaptured(DOLEvent e, object sender, EventArgs args)
        {
            // DOL and OpenDAoC both fire KeepEvent.KeepTaken globally with
            // KeepEventArgs; the global event sender itself is null.
            var keepArgs = args as KeepEventArgs;
            var keep = keepArgs?.Keep ?? sender as AbstractGameKeep;
            if (keep == null) return;

            string realm;
            switch (keep.Realm)
            {
                case eRealm.Albion:
                    realm = "Albion";
                    break;
                case eRealm.Midgard:
                    realm = "Midgard";
                    break;
                case eRealm.Hibernia:
                    realm = "Hibernia";
                    break;
                default:
                    realm = "Neutral";
                    break;
            }

            string msg = $"{keep.Name} has been captured by {realm}!";
            SendEventAsync("keep", msg);
        }

        private static void SendEventAsync(string type, string message)
        {
            Task.Run(async () =>
            {
                try
                {
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("secret", BRIDGE_SECRET),
                        new KeyValuePair<string, string>("type", type),
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
    }
}
