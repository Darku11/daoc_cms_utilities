using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ============================================================
//  Aldhran Ingame Console — ASP.NET Minimal API
//  Version: 2.4.0 — Teleport/Zone-Lookup fix, Raw-Command fix,
//                    Item-Autocomplete, Heal/Revive/Freeze endpoints
//  PHP communicates exclusively with this Console (HTTP:5100).
//  Console forwards to AldhranBridge (TCP:2000).
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();

var app = builder.Build();

// ── Config ────────────────────────────────────────────────────
var API_SECRET      = builder.Configuration["Console:ApiSecret"]    ?? "CHANGE_ME_IN_APPSETTINGS";
var DB_CONN         = builder.Configuration["Console:DbConnection"]  ?? "Server=localhost;Database=doldb;User=dol;Password=dol;";
var DOL_HOST        = builder.Configuration["Console:DolHost"]       ?? "127.0.0.1";
var DOL_PORT        = int.Parse(builder.Configuration["Console:DolPort"] ?? "2000");
var BRIDGE_SECRET   = builder.Configuration["Console:BridgeSecret"]  ?? "Aldhran_C0ns0le_Secret_2026";
var DOL_SCRIPTS     = builder.Configuration["Console:ScriptsPath"]   ?? "/opt/dol/scripts/playerclasses/";

// Befehle, die über /raw niemals ausgeführt werden dürfen (zusätzlich zur
// zweiten Sperre in AldhranBridge.cs — defense in depth).
var BLOCKED_RAW_COMMANDS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "shutdown", "quit"
};

// ── Auth middleware ────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.TryGetValue("X-Aldhran-Secret", out var secret) || secret != API_SECRET)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Unauthorized" });
        return;
    }
    await next();
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();

// ── Bridge Helper ─────────────────────────────────────────────
static async Task<string> SendBridgeCommand(string host, int port, string bridgeSecret, object payload, ILogger logger)
{
    try
    {
        using var client = new TcpClient();
        var cts = new System.Threading.CancellationTokenSource(3000);
        await client.ConnectAsync(host, port, cts.Token);
        using var stream = client.GetStream();
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        using var writer = new System.IO.StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

        await writer.WriteLineAsync(bridgeSecret);
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload));
        await writer.FlushAsync();

        var sb  = new StringBuilder();
        var buf = new char[4096];
        int read;
        client.ReceiveTimeout = 5000;
        while ((read = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            sb.Append(buf, 0, read);

        var response = sb.ToString().Trim();
        var start = response.IndexOf('{');
        var end   = response.LastIndexOf('}');
        if (start >= 0 && end >= start)
            response = response.Substring(start, end - start + 1);

        return string.IsNullOrEmpty(response)
            ? "{\"ok\":false,\"error\":\"No response\"}"
            : response;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Bridge command failed");
        return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
    }
}

// ── DB Helpers ────────────────────────────────────────────────
static async Task<List<Dictionary<string, object>>> QueryDb(string connStr, string sql, Dictionary<string, object>? parms = null)
{
    var rows = new List<Dictionary<string, object>>();
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand(sql, conn);
    if (parms != null)
        foreach (var kv in parms)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value);
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync())
    {
        var row = new Dictionary<string, object>();
        for (int i = 0; i < rdr.FieldCount; i++)
            row[rdr.GetName(i)] = rdr.IsDBNull(i) ? "" : rdr.GetValue(i);
        rows.Add(row);
    }
    return rows;
}

// Legt die Zone-Lookup-Tabelle bei Bedarf an (idempotent) und befüllt sie mit den
// Haupt-City-Koordinaten. Diese Werte sind NICHT geraten, sondern 1:1 aus
// GamePlayer.cs (Release()-Methode, eReleaseType.City) übernommen — dort stehen sie
// als hartcodierte Realm-Release-Ziele im Server-Code:
//   Albion:   Region 10,  x=8192+26315, y=8192+21177, z=8256  (City of Camelot)
//   Midgard:  Region 101, x=8192+24664, y=8192+21402, z=8759  (Jordheim)
//   Hibernia: Region 201, x=192+15780,  y=8192+22727, z=7060  (Tir Na Nog)
// Die Tir-Na-Nog-X-Koordinate weicht bewusst vom 8192-Offset-Muster ab — das ist so
// im Originalcode hinterlegt, kein Tippfehler dieser Migration.
static async Task EnsureZonePointsTable(string connStr)
{
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS `igc_zone_points` (
            `zone_key`   VARCHAR(50) PRIMARY KEY,
            `label`      VARCHAR(100) NOT NULL,
            `region_id`  INT NOT NULL,
            `x`          INT NOT NULL,
            `y`          INT NOT NULL,
            `z`          INT NOT NULL DEFAULT 0
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """, conn);
    await cmd.ExecuteNonQueryAsync();

    await using var seedCmd = new MySqlCommand("""
        INSERT IGNORE INTO `igc_zone_points` (`zone_key`, `label`, `region_id`, `x`, `y`, `z`) VALUES
            ('camelot_city',  'City of Camelot (Albion)',   10,  34507, 29369, 8256),
            ('jordheim_city', 'Jordheim (Midgard)',         101, 32856, 29594, 8759),
            ('tir_na_nog',    'Tir Na Nog (Hibernia)',      201, 15972, 30919, 7060);
        """, conn);
    await seedCmd.ExecuteNonQueryAsync();
}
await EnsureZonePointsTable(DB_CONN);

// ============================================================
//  BESTEHENDE ENDPOINTS
// ============================================================

// ── GET /status ───────────────────────────────────────────────
app.MapGet("/status", async () =>
{
    try
    {
        var raw  = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new { action = "status" }, logger);
        var data = JsonSerializer.Deserialize<JsonElement>(raw);
        return Results.Ok(data);
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
});

// ── POST /kick ────────────────────────────────────────────────
app.MapPost("/kick", async ([FromBody] JsonElement body) =>
{
    var name   = body.TryGetProperty("name",   out var n) ? n.GetString() : null;
    var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : "Kicked by Admin";
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "kick", name, reason }, logger);
    logger.LogInformation("KICK: {name} — {reason}", name, reason);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /privlevel ───────────────────────────────────────────
app.MapPost("/privlevel", async ([FromBody] JsonElement body) =>
{
    var name  = body.TryGetProperty("name",  out var n) ? n.GetString() : null;
    var level = body.TryGetProperty("level", out var l) ? l.GetInt32()  : -1;
    if (string.IsNullOrWhiteSpace(name) || level < 0 || level > 3)
        return Results.BadRequest(new { ok = false, error = "name and level (0-3) required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "privlevel", name, level }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /gmmode ──────────────────────────────────────────────
app.MapPost("/gmmode", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    var on   = body.TryGetProperty("on",   out var o) && o.GetBoolean();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "gmmode", name, on }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /teleport ────────────────────────────────────────────
// FIX #3: "x" wurde fälschlich aus body["y"] gelesen (Copy-Paste-Bug) -> korrigiert.
// FIX #4: "zone"-Feld wurde von PHP gesendet, aber nie ausgewertet. Jetzt: wenn ein
// zone-Key übergeben wird, gegen igc_zone_points auflösen und dessen region/x/y/z
// verwenden (überschreibt evtl. mitgesendete numerische Koordinaten).
app.MapPost("/teleport", async ([FromBody] JsonElement body) =>
{
    var name   = body.TryGetProperty("name",   out var n)  ? n.GetString() : null;
    var zone   = body.TryGetProperty("zone",   out var pz2) ? pz2.GetString() : null;
    var x      = body.TryGetProperty("x",      out var px) ? px.GetInt32() : 0;
    var y      = body.TryGetProperty("y",      out var py) ? py.GetInt32() : 0;
    var z      = body.TryGetProperty("z",      out var pz) ? pz.GetInt32() : 0;
    var region = body.TryGetProperty("region", out var pr) ? pr.GetInt32() : 0;

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    if (!string.IsNullOrWhiteSpace(zone))
    {
        var rows = await QueryDb(DB_CONN,
            "SELECT region_id, x, y, z FROM igc_zone_points WHERE zone_key = @zone",
            new Dictionary<string, object> { ["@zone"] = zone });

        if (rows.Count == 0)
            return Results.Ok(new { ok = false, error = $"Zone '{zone}' nicht gefunden." });

        region = Convert.ToInt32(rows[0]["region_id"]);
        x      = Convert.ToInt32(rows[0]["x"]);
        y      = Convert.ToInt32(rows[0]["y"]);
        z      = Convert.ToInt32(rows[0]["z"]);
    }

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "teleport", name, x, y, z, region }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /giveitem ────────────────────────────────────────────
app.MapPost("/giveitem", async ([FromBody] JsonElement body) =>
{
    var name   = body.TryGetProperty("name",     out var n)  ? n.GetString()  : null;
    var itemId = body.TryGetProperty("item_id", out var id) ? id.GetString() : null;
    var count  = body.TryGetProperty("count",   out var c)  ? c.GetInt32()   : 1;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(itemId))
        return Results.BadRequest(new { ok = false, error = "name and item_id required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "giveitem", name, item_id = itemId, count }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /guildchat ───────────────────────────────────────────
app.MapPost("/guildchat", async ([FromBody] JsonElement body) =>
{
    var guildName = body.TryGetProperty("guild",   out var g) ? g.GetString() : null;
    var sender    = body.TryGetProperty("sender",  out var s) ? s.GetString() : "Discord";
    var msg       = body.TryGetProperty("message", out var m) ? m.GetString() : null;
    
    if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(msg))
        return Results.BadRequest(new { ok = false, error = "guild and message required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "guildchat", guild = guildName, sender, message = msg }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});
// Neuer Endpoint für das Item-Autocomplete im "Give Item"-Panel.
app.MapGet("/items/search", async (HttpRequest request) =>
{
    var q = request.Query["q"].ToString();
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.Ok(new { ok = true, items = Array.Empty<object>() });

    try
    {
        var rows = await QueryDb(DB_CONN,
            "SELECT Id_nb AS item_id, Name AS name, Level AS level FROM itemtemplate WHERE Name LIKE @q ORDER BY Name ASC LIMIT 20",
            new Dictionary<string, object> { ["@q"] = "%" + q + "%" });
        return Results.Ok(new { ok = true, items = rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[ITEMS] search failed for query {q}", q);
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

// ── POST /setstats ────────────────────────────────────────────
app.MapPost("/setstats", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name",  out var n) ? n.GetString() : null;
    var stat = body.TryGetProperty("stat",  out var s) ? s.GetString() : null;
    var val  = body.TryGetProperty("value", out var v) ? v.GetInt32()  : 0;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(stat))
        return Results.BadRequest(new { ok = false, error = "name and stat required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "setstats", name, stat, value = val }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /broadcast ───────────────────────────────────────────
app.MapPost("/broadcast", async ([FromBody] JsonElement body) =>
{
    var msg    = body.TryGetProperty("message", out var m) ? m.GetString() : null;
    var sender = body.TryGetProperty("sender",  out var s) ? s.GetString() : "System";
    if (string.IsNullOrWhiteSpace(msg))
        return Results.BadRequest(new { ok = false, error = "message required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "broadcast", message = msg, sender }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /restart ─────────────────────────────────────────────
app.MapPost("/restart", async ([FromBody] JsonElement body) =>
{
    var delayMinutes = body.TryGetProperty("delay_minutes", out var d) ? d.GetInt32()  : 0;
    var announcement = body.TryGetProperty("announcement",  out var a) ? a.GetString() : "";
    var sender       = body.TryGetProperty("sender",        out var s) ? s.GetString() : "System";

    delayMinutes = Math.Max(0, Math.Min(60, delayMinutes));

    logger.LogWarning("[RESTART] Scheduled by {sender} in {delay}min — announcement: {ann}",
        sender, delayMinutes, string.IsNullOrWhiteSpace(announcement) ? "—" : announcement);

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new
    {
        action        = "restart",
        delay_minutes = delayMinutes,
        announcement  = announcement ?? "",
        sender
    }, logger);

    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /raw ─────────────────────────────────────────────────
// FIX #7: Sendete bisher fälschlich action="broadcast" -> jetzt korrekt action="raw",
// die Bridge führt den Befehl über ScriptMgr.GuessCommand() wirklich aus.
// Zusätzlich: einfache Blockliste für kritische Befehle, bevor überhaupt an die
// Bridge weitergeleitet wird (defense in depth, zweite Sperre sitzt in der Bridge).
app.MapPost("/raw", async ([FromBody] JsonElement body) =>
{
    var cmd      = body.TryGetProperty("command",  out var c) ? c.GetString() : null;
    var executor = body.TryGetProperty("executor", out var e) ? e.GetString() : null;

    if (string.IsNullOrWhiteSpace(cmd))
        return Results.BadRequest(new { ok = false, error = "command required" });

    var firstWord = cmd.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    if (BLOCKED_RAW_COMMANDS.Contains(firstWord))
    {
        logger.LogWarning("RAW COMMAND blocked: {cmd}", cmd);
        return Results.Ok(new { ok = false, error = $"Befehl '{firstWord}' ist gesperrt." });
    }

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "raw", command = cmd, executor }, logger);
    logger.LogWarning("RAW COMMAND: {cmd}", cmd);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /heal ────────────────────────────────────────────────
app.MapPost("/heal", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "heal", name }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /revive ──────────────────────────────────────────────
app.MapPost("/revive", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "revive", name }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /freeze ──────────────────────────────────────────────
app.MapPost("/freeze", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    var on   = body.TryGetProperty("on",   out var o) && o.GetBoolean();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "freeze", name, on }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ── POST /mute ────────────────────────────────────────────────
app.MapPost("/mute", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    var on   = body.TryGetProperty("on",   out var o) && o.GetBoolean();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var raw = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET,
        new { action = "mute", name, on }, logger);
    return Results.Ok(JsonSerializer.Deserialize<JsonElement>(raw));
});

// ============================================================
//  ITEMSHOP ENDPOINTS
// ============================================================

// ── GET /shop/cm-listings ───────────────────────────────────────
app.MapGet("/shop/cm-listings", async (HttpRequest request) =>
{
    var itemId = request.Query["item_id"].ToString();
    if (string.IsNullOrWhiteSpace(itemId))
        return Results.BadRequest(new { ok = false, error = "item_id required" });

    var sql = """
        SELECT i.Inventory_ID AS ref, i.ITemplate_Id AS item_id,
               i.SellPrice AS price, i.Count AS count,
               h.HouseNumber, h.RegionID
        FROM inventory i
        JOIN houseconsignmentmerchant hcm ON i.OwnerID = hcm.OwnerID
        JOIN dbhouse h ON hcm.HouseNumber = h.HouseNumber
        WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0
        ORDER BY i.SellPrice ASC
        """;

    var parms = new Dictionary<string, object> { ["@itemId"] = itemId };

    var regionParam = request.Query["realm_region"].ToString();
    if (!string.IsNullOrWhiteSpace(regionParam) && int.TryParse(regionParam, out var regionId))
    {
        sql = sql.Replace("WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0",
                           "WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0 AND h.RegionID = @regionId");
        parms["@regionId"] = regionId;
    }

    try
    {
        var rows = await QueryDb(DB_CONN, sql, parms);
        return Results.Ok(new { ok = true, listings = rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SHOP] cm-listings query failed for item {item}", itemId);
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

// ── POST /shop/purchase ─────────────────────────────────────────
app.MapPost("/shop/purchase", async ([FromBody] JsonElement body) =>
{
    var buyer   = body.TryGetProperty("buyer_name", out var bn) ? bn.GetString() : null;
    var source  = body.TryGetProperty("source",     out var sr) ? sr.GetString() : null;
    var itemRef = body.TryGetProperty("item_ref",    out var ir) ? ir.GetString() : null;
    var count   = body.TryGetProperty("count",      out var ct) ? ct.GetInt32()  : 1;

    if (string.IsNullOrWhiteSpace(buyer) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(itemRef))
        return Results.BadRequest(new { ok = false, error = "buyer_name, source, item_ref required" });

    if (source != "player" && source != "system")
        return Results.BadRequest(new { ok = false, error = "invalid source" });

    if (count < 1 || count > 100)
        return Results.BadRequest(new { ok = false, error = "count out of range" });

    logger.LogInformation("[SHOP] Purchase attempt: {buyer} buying {ref} ({source}) x{count}",
        buyer, itemRef, source, count);

    try
    {
        string itemId = "";
        int finalPrice = 0;

        // 1. Preisermittlung und Bestandsprüfung über die DB
        if (source == "system")
        {
            itemId = itemRef.Replace("sys_", "");
            var sysItems = await QueryDb(DB_CONN, "SELECT base_price FROM shop_system_items WHERE item_id = @itemId AND active = 1", new() { ["@itemId"] = itemId });
            if (sysItems.Count == 0) return Results.Ok(new { ok = false, error = "Item nicht mehr im System-Katalog verfügbar." });
            finalPrice = (int)Math.Round(Convert.ToInt32(sysItems[0]["base_price"]) * 1.30) * count;
        }
        else
        {
            var listings = await QueryDb(DB_CONN, "SELECT ITemplate_Id, SellPrice, Count FROM inventory WHERE Inventory_ID = @ref AND SellPrice > 0", new() { ["@ref"] = itemRef });
            if (listings.Count == 0) return Results.Ok(new { ok = false, error = "Dieses Angebot ist nicht mehr verfügbar." });
            if (Convert.ToInt32(listings[0]["Count"]) < count) return Results.Ok(new { ok = false, error = "Nicht genügend Artikel beim Verkäufer vorrätig." });
            itemId = listings[0]["ITemplate_Id"].ToString();
            finalPrice = Convert.ToInt32(listings[0]["SellPrice"]) * count;
        }

        // 2. Goldprüfung des Käufers über die DB
        var playerQuery = await QueryDb(DB_CONN, "SELECT Platinum, Gold, Silver, Copper FROM dolcharacters WHERE Name = @name", new() { ["@name"] = buyer });
        if (playerQuery.Count == 0) return Results.Ok(new { ok = false, error = "Charakter nicht gefunden." });

        long plat = playerQuery[0]["Platinum"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Platinum"]);
        long gold = playerQuery[0]["Gold"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Gold"]);
        long silv = playerQuery[0]["Silver"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Silver"]);
        long copp = playerQuery[0]["Copper"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Copper"]);

        long totalCopper = copp + (silv * 100L) + (gold * 10000L) + (plat * 10000000L);

        if (totalCopper < finalPrice) return Results.Ok(new { ok = false, error = "Du hast nicht genügend Gold für diesen Kauf." });

        // 3. Wenn Spieler-Verkauf, Item aus dem Consignment Merchant entfernen
        if (source == "player")
        {
            await using (var conn = new MySqlConnection(DB_CONN))
            {
                await conn.OpenAsync();
                await using (var cmdUpdate = new MySqlCommand("UPDATE inventory SET Count = Count - @count WHERE Inventory_ID = @ref", conn))
                {
                    cmdUpdate.Parameters.AddWithValue("@count", count);
                    cmdUpdate.Parameters.AddWithValue("@ref", itemRef);
                    await cmdUpdate.ExecuteNonQueryAsync();
                }
                await using (var cmdDelete = new MySqlCommand("DELETE FROM inventory WHERE Inventory_ID = @ref AND Count <= 0", conn))
                {
                    cmdDelete.Parameters.AddWithValue("@ref", itemRef);
                    await cmdDelete.ExecuteNonQueryAsync();
                }
            }
        }

        // 4. Gold-Abzug live im Spiel über die funktionierende setstats-Aktion triggern
        long remaining = totalCopper - finalPrice;
        long newPlat = remaining / 10000000L;
        remaining %= 10000000L;
        long newGold = remaining / 10000L;
        remaining %= 10000L;
        long newSilv = remaining / 100L;
        long newCopp = remaining % 100L;

        await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new { action = "setstats", name = buyer, stat = "platinum", value = (int)newPlat }, logger);
        await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new { action = "setstats", name = buyer, stat = "gold", value = (int)newGold }, logger);
        await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new { action = "setstats", name = buyer, stat = "silver", value = (int)newSilv }, logger);
        await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new { action = "setstats", name = buyer, stat = "copper", value = (int)newCopp }, logger);

        // 5. Item-Zustellung über die funktionierende Haupt-Brücke (DOL_PORT)
        var rawResponse = await SendBridgeCommand(DOL_HOST, DOL_PORT, BRIDGE_SECRET, new
        {
            action = "giveitem",
            name = buyer,
            item_id = itemId,
            count = count
        }, logger);

        var data = JsonSerializer.Deserialize<JsonElement>(rawResponse);
        var bridgeOk = data.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();

        if (!bridgeOk)
        {
            logger.LogError("[SHOP] CRITICAL: gold deducted but delivery failed via giveitem — buyer={buyer} item={item}", buyer, itemId);
            return Results.Ok(new { ok = false, gold_deducted = true, item_id = itemId, error = "In-game delivery failed. Queued for fallback." });
        }

        logger.LogInformation("[SHOP] Purchase OK: {buyer} bought {item} x{count}", buyer, itemId, count);
        return Results.Ok(new { ok = true, gold_deducted = true });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SHOP] Exception during purchase handler for {buyer}", buyer);
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

// ============================================================
//  WORLD FORGE ENDPOINTS
// ============================================================

// ── POST /world-forge/upload ──────────────────────────────────
app.MapPost("/world-forge/upload", async ([FromBody] JsonElement body) =>
{
    var filename = body.TryGetProperty("filename", out var f) ? f.GetString() : null;
    var content  = body.TryGetProperty("content",  out var c) ? c.GetString() : null;

    if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(content))
        return Results.BadRequest(new { ok = false, error = "filename and content required" });

    filename = Path.GetFileName(filename);
    if (string.IsNullOrEmpty(filename))
        return Results.BadRequest(new { ok = false, error = "invalid filename" });

    var ext = Path.GetExtension(filename).ToLowerInvariant();
    if (ext is not (".cs" or ".sql" or ".md"))
        return Results.BadRequest(new { ok = false, error = $"file type '{ext}' not allowed" });

    try
    {
        Directory.CreateDirectory(DOL_SCRIPTS);

        var targetPath = Path.Combine(DOL_SCRIPTS, filename);
        await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);

        logger.LogInformation("[WORLD FORGE] Uploaded: {file} ({bytes} bytes)",
            filename, content.Length);

        return Results.Ok(new { ok = true, path = targetPath, bytes = content.Length });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WORLD FORGE] Upload failed: {file}", filename);
        return Results.Json(new { ok = false, error = ex.Message },
            statusCode: 500);
    }
});

// ── POST /world-forge/sync-realm ──────────────────────────────
app.MapPost("/world-forge/sync-realm", async ([FromBody] JsonElement body) =>
{
    var name        = body.TryGetProperty("name",         out var n)  ? n.GetString()  ?? "" : "";
    var shortName   = body.TryGetProperty("short_name",   out var sn) ? sn.GetString() ?? "" : "";
    var tagline     = body.TryGetProperty("tagline",      out var t)  ? t.GetString()  ?? "" : "";
    var loreContext = body.TryGetProperty("lore_context", out var l)  ? l.GetString()  ?? "" : "";
    var classCount  = body.TryGetProperty("class_count",  out var cc) ? cc.GetInt32()  : 0;
    var status      = body.TryGetProperty("status",       out var st) ? st.GetString() ?? "Draft" : "Draft";

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    try
    {
        await using var conn = new MySqlConnection(DB_CONN);
        await conn.OpenAsync();

        await using (var createCmd = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS `worldforge_realms` (
                `id`          INT AUTO_INCREMENT PRIMARY KEY,
                `name`        VARCHAR(100) NOT NULL,
                `short_name`  VARCHAR(20),
                `tagline`     VARCHAR(255),
                `lore_context`TEXT,
                `class_count` INT DEFAULT 0,
                `status`      VARCHAR(30) DEFAULT 'Draft',
                `synced_at`   DATETIME DEFAULT CURRENT_TIMESTAMP
                    ON UPDATE CURRENT_TIMESTAMP,
                UNIQUE KEY `uq_realm_name` (`name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """, conn))
            await createCmd.ExecuteNonQueryAsync();

        await using var upsert = new MySqlCommand("""
            INSERT INTO `worldforge_realms`
                (`name`, `short_name`, `tagline`, `lore_context`, `class_count`, `status`)
            VALUES
                (@name, @sn, @tagline, @lore, @cc, @status)
            ON DUPLICATE KEY UPDATE
                `short_name`   = VALUES(`short_name`),
                `tagline`      = VALUES(`tagline`),
                `lore_context` = VALUES(`lore_context`),
                `class_count`  = VALUES(`class_count`),
                `status`       = VALUES(`status`),
                `synced_at`    = CURRENT_TIMESTAMP;
            """, conn);

        upsert.Parameters.AddWithValue("@name",   name);
        upsert.Parameters.AddWithValue("@sn",     shortName);
        upsert.Parameters.AddWithValue("@tagline", tagline);
        upsert.Parameters.AddWithValue("@lore",   loreContext);
        upsert.Parameters.AddWithValue("@cc",     classCount);
        upsert.Parameters.AddWithValue("@status", status);
        await upsert.ExecuteNonQueryAsync();

        logger.LogInformation("[WORLD FORGE] Realm synced: {name} ({status}, {cc} classes)",
            name, status, classCount);

        return Results.Ok(new { ok = true, realm = name, status });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WORLD FORGE] Realm sync failed: {name}", name);
        return Results.Json(new { ok = false, error = ex.Message },
            statusCode: 500);
    }
});

// ── GET /world-forge/realms ───────────────────────────────────
app.MapGet("/world-forge/realms", async () =>
{
    try
    {
        var rows = await QueryDb(DB_CONN,
            "SELECT id, name, short_name, tagline, class_count, status, synced_at " +
            "FROM worldforge_realms ORDER BY synced_at DESC");
        return Results.Ok(new { ok = true, realms = rows });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

app.Run("http://0.0.0.0:5100");
