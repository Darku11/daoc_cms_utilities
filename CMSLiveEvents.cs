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

        // Must match "Bridge Secret" under ACP -> General Settings -> Bridge Connection
        // (game_server_bridge_secret).
        private const string BRIDGE_SECRET = "CHANGE_ME_BRIDGE_SECRET";

        [ScriptLoadedEvent]
        public static void OnScriptCompiled(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.AddHandler(GameLivingEvent.Dying, OnPlayerDying);
            // Keep-capture events vary by DOL version — wire this up once you've
            // confirmed the event name for your fork, e.g. GameKeepEvent.Captured.
            // GameEventMgr.AddHandler(GameKeepEvent.Captured, OnKeepCaptured);
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnPlayerDying);
            // GameEventMgr.RemoveHandler(GameKeepEvent.Captured, OnKeepCaptured);
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
            var keep = sender as AbstractGameKeep;
            if (keep == null) return;

            string realm = keep.Realm == eRealm.Albion ? "Albion" : keep.Realm == eRealm.Midgard ? "Midgard" : "Hibernia";
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
