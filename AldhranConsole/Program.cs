/* SPDX-License-Identifier: GPL-3.0-only */
using AldhranConsole;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

// ============================================================
//  Aldhran Ingame Console — ASP.NET Minimal API
//  Version: 2.5.0 — unified configuration, hardened authentication,
//                    shared CMS client and DOL/OpenDAoC documentation
//  PHP communicates exclusively with this Console (HTTP:5100).
//  Console forwards to AldhranBridge (TCP:2000).
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();

ConsoleOptions options = ConsoleOptions.Load(builder.Configuration);
options.Validate();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<BridgeClient>();
builder.Services.AddSingleton<GameDatabase>();

var app = builder.Build();
var bridge = app.Services.GetRequiredService<BridgeClient>();
var database = app.Services.GetRequiredService<GameDatabase>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
string serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

try
{
    await database.EnsureSchemaAsync();
}
catch (Exception ex)
{
    logger.LogError(
        ex,
        "Database initialization failed. Bridge-only endpoints remain available; database-backed endpoints will fail until the connection is restored.");
}

// Commands that must never be executed through /raw. AldhranBridge applies
// the same independent blocklist as defense in depth.
var blockedRawCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "shutdown", "quit"
};

static bool BridgeSucceeded(JsonElement response)
    => response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty("ok", out JsonElement ok)
        && ok.ValueKind == JsonValueKind.True;

static async Task<bool> SetMoneyAsync(
    BridgeClient bridgeClient,
    string characterName,
    long platinum,
    long gold,
    long silver,
    long copper)
{
    (string Stat, long Value)[] values =
    {
        ("platinum", platinum),
        ("gold", gold),
        ("silver", silver),
        ("copper", copper)
    };

    foreach ((string stat, long value) in values)
    {
        if (value is < 0 or > int.MaxValue)
            return false;

        JsonElement response = await bridgeClient.SendAsync(new
        {
            action = "setstats",
            name = characterName,
            stat,
            value = (int)value
        });

        if (!BridgeSucceeded(response))
            return false;
    }

    return true;
}

app.UseMiddleware<SecretAuthenticationMiddleware>();

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "AldhranConsole",
    version = serviceVersion
}));

// ============================================================
//  LIVE ADMIN ENDPOINTS
// ============================================================

// ── GET /status ───────────────────────────────────────────────
app.MapGet("/status", async () =>
{
    return Results.Ok(await bridge.SendAsync(new { action = "status" }));
});

// ── POST /kick ────────────────────────────────────────────────
app.MapPost("/kick", async ([FromBody] JsonElement body) =>
{
    var name   = body.TryGetProperty("name",   out var n) ? n.GetString() : null;
    var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : "Kicked by Admin";
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    var result = await bridge.SendAsync(new { action = "kick", name, reason });
    logger.LogInformation("KICK: {name} — {reason}", name, reason);
    return Results.Ok(result);
});

// ── POST /privlevel ───────────────────────────────────────────
app.MapPost("/privlevel", async ([FromBody] JsonElement body) =>
{
    var name  = body.TryGetProperty("name",  out var n) ? n.GetString() : null;
    var level = body.TryGetProperty("level", out var l) ? l.GetInt32()  : -1;
    if (string.IsNullOrWhiteSpace(name) || level < 0 || level > 3)
        return Results.BadRequest(new { ok = false, error = "name and level (0-3) required" });

    return Results.Ok(await bridge.SendAsync(new { action = "privlevel", name, level }));
});

// ── POST /teleport ────────────────────────────────────────────
// A named zone is resolved through igc_zone_points and takes precedence over
// numeric coordinates supplied in the same request.
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
        var rows = await database.QueryAsync(
            "SELECT region_id, x, y, z FROM igc_zone_points WHERE zone_key = @zone",
            new Dictionary<string, object> { ["@zone"] = zone });

        if (rows.Count == 0)
            return Results.Ok(new { ok = false, error = $"Zone '{zone}' was not found." });

        region = Convert.ToInt32(rows[0]["region_id"]);
        x      = Convert.ToInt32(rows[0]["x"]);
        y      = Convert.ToInt32(rows[0]["y"]);
        z      = Convert.ToInt32(rows[0]["z"]);
    }

    return Results.Ok(await bridge.SendAsync(new { action = "teleport", name, x, y, z, region }));
});

// ── POST /giveitem ────────────────────────────────────────────
app.MapPost("/giveitem", async ([FromBody] JsonElement body) =>
{
    var name   = body.TryGetProperty("name",     out var n)  ? n.GetString()  : null;
    var itemId = body.TryGetProperty("item_id", out var id) ? id.GetString() : null;
    var count  = body.TryGetProperty("count",   out var c)  ? c.GetInt32()   : 1;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(itemId))
        return Results.BadRequest(new { ok = false, error = "name and item_id required" });
    if (count < 1 || count > 100)
        return Results.BadRequest(new { ok = false, error = "count must be between 1 and 100" });

    return Results.Ok(await bridge.SendAsync(new { action = "giveitem", name, item_id = itemId, count }));
});

// ── POST /guildchat ───────────────────────────────────────────
app.MapPost("/guildchat", async ([FromBody] JsonElement body) =>
{
    var guildName = body.TryGetProperty("guild",   out var g) ? g.GetString() : null;
    var sender    = body.TryGetProperty("sender",  out var s) ? s.GetString() : "Discord";
    var msg       = body.TryGetProperty("message", out var m) ? m.GetString() : null;
    
    if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(msg))
        return Results.BadRequest(new { ok = false, error = "guild and message required" });

    return Results.Ok(await bridge.SendAsync(
        new { action = "guildchat", guild = guildName, sender, message = msg }));
});
// Item autocomplete for the ACP "Give Item" panel.
app.MapGet("/items/search", async (HttpRequest request) =>
{
    var q = request.Query["q"].ToString();
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.Ok(new { ok = true, items = Array.Empty<object>() });

    try
    {
        var rows = await database.QueryAsync(
            "SELECT Id_nb AS item_id, Name AS name, Level AS level FROM itemtemplate WHERE Name LIKE @q ORDER BY Name ASC LIMIT 20",
            new Dictionary<string, object> { ["@q"] = "%" + q + "%" });
        return Results.Ok(new { ok = true, items = rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[ITEMS] search failed for query {q}", q);
        return Results.Json(new { ok = false, error = "The item search failed." }, statusCode: 500);
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

    return Results.Ok(await bridge.SendAsync(new { action = "setstats", name, stat, value = val }));
});

// ── POST /broadcast ───────────────────────────────────────────
app.MapPost("/broadcast", async ([FromBody] JsonElement body) =>
{
    var msg    = body.TryGetProperty("message", out var m) ? m.GetString() : null;
    var sender = body.TryGetProperty("sender",  out var s) ? s.GetString() : "System";
    if (string.IsNullOrWhiteSpace(msg))
        return Results.BadRequest(new { ok = false, error = "message required" });

    return Results.Ok(await bridge.SendAsync(new { action = "broadcast", message = msg, sender }));
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

    var result = await bridge.SendAsync(new
    {
        action        = "restart",
        delay_minutes = delayMinutes,
        announcement  = announcement ?? "",
        sender
    });

    return Results.Ok(result);
});

// ── POST /raw ─────────────────────────────────────────────────
// The bridge resolves the command through ScriptMgr.GuessCommand(). Critical
// commands are rejected here before they reach the second blocklist in the bridge.
app.MapPost("/raw", async ([FromBody] JsonElement body) =>
{
    var cmd      = body.TryGetProperty("command",  out var c) ? c.GetString() : null;
    var executor = body.TryGetProperty("executor", out var e) ? e.GetString() : null;

    if (string.IsNullOrWhiteSpace(cmd))
        return Results.BadRequest(new { ok = false, error = "command required" });

    var firstWord = cmd.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    if (blockedRawCommands.Contains(firstWord))
    {
        logger.LogWarning("RAW COMMAND blocked: {cmd}", cmd);
        return Results.Ok(new { ok = false, error = $"Command '{firstWord}' is blocked." });
    }

    var result = await bridge.SendAsync(new { action = "raw", command = cmd, executor });
    logger.LogWarning("RAW COMMAND: {cmd}", cmd);
    return Results.Ok(result);
});

// ── POST /heal ────────────────────────────────────────────────
app.MapPost("/heal", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    return Results.Ok(await bridge.SendAsync(new { action = "heal", name }));
});

// ── POST /revive ──────────────────────────────────────────────
app.MapPost("/revive", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    return Results.Ok(await bridge.SendAsync(new { action = "revive", name }));
});

// ── POST /freeze ──────────────────────────────────────────────
app.MapPost("/freeze", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    var on   = body.TryGetProperty("on",   out var o) && o.GetBoolean();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    return Results.Ok(await bridge.SendAsync(new { action = "freeze", name, on }));
});

// ── POST /mute ────────────────────────────────────────────────
app.MapPost("/mute", async ([FromBody] JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    var on   = body.TryGetProperty("on",   out var o) && o.GetBoolean();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { ok = false, error = "name required" });

    return Results.Ok(await bridge.SendAsync(new { action = "mute", name, on }));
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
        WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0 AND i.Count > 0
        ORDER BY i.SellPrice ASC
        """;

    var parms = new Dictionary<string, object> { ["@itemId"] = itemId };

    var regionParam = request.Query["realm_region"].ToString();
    if (!string.IsNullOrWhiteSpace(regionParam) && int.TryParse(regionParam, out var regionId))
    {
        sql = sql.Replace(
            "WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0 AND i.Count > 0",
            "WHERE i.ITemplate_Id = @itemId AND i.SellPrice > 0 AND i.Count > 0 AND h.RegionID = @regionId");
        parms["@regionId"] = regionId;
    }

    try
    {
        var rows = await database.QueryAsync(sql, parms);
        return Results.Ok(new { ok = true, listings = rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SHOP] cm-listings query failed for item {item}", itemId);
        return Results.Json(new { ok = false, error = "The listings could not be loaded." }, statusCode: 500);
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

    bool listingReserved = false;
    bool itemDelivered = false;
    bool balanceChanged = false;
    long originalPlatinum = 0;
    long originalGold = 0;
    long originalSilver = 0;
    long originalCopper = 0;

    try
    {
        string itemId;
        long finalPrice;

        // 1. Resolve price and availability from the game database.
        if (source == "system")
        {
            itemId = itemRef.StartsWith("sys_", StringComparison.Ordinal) ? itemRef[4..] : itemRef;
            var sysItems = await database.QueryAsync(
                "SELECT base_price FROM shop_system_items WHERE item_id = @itemId AND active = 1",
                new Dictionary<string, object> { ["@itemId"] = itemId });
            if (sysItems.Count == 0) return Results.Ok(new { ok = false, error = "The item is no longer available in the system catalogue." });
            finalPrice = Convert.ToInt64(
                Math.Round(Convert.ToInt64(sysItems[0]["base_price"]) * 1.30)) * count;
        }
        else
        {
            var listings = await database.QueryAsync(
                "SELECT ITemplate_Id, SellPrice, Count FROM inventory WHERE Inventory_ID = @ref AND SellPrice > 0",
                new Dictionary<string, object> { ["@ref"] = itemRef });
            if (listings.Count == 0) return Results.Ok(new { ok = false, error = "The listing is no longer available." });
            if (Convert.ToInt32(listings[0]["Count"]) < count) return Results.Ok(new { ok = false, error = "The seller does not have enough items in stock." });
            itemId = listings[0]["ITemplate_Id"].ToString() ?? "";
            finalPrice = Convert.ToInt64(listings[0]["SellPrice"]) * count;
        }

        // 2. Verify the buyer's balance in the game database.
        var playerQuery = await database.QueryAsync(
            "SELECT Platinum, Gold, Silver, Copper FROM dolcharacters WHERE Name = @name",
            new Dictionary<string, object> { ["@name"] = buyer });
        if (playerQuery.Count == 0) return Results.Ok(new { ok = false, error = "Character not found." });

        long plat = playerQuery[0]["Platinum"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Platinum"]);
        long gold = playerQuery[0]["Gold"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Gold"]);
        long silv = playerQuery[0]["Silver"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Silver"]);
        long copp = playerQuery[0]["Copper"].ToString() == "" ? 0 : Convert.ToInt64(playerQuery[0]["Copper"]);
        originalPlatinum = plat;
        originalGold = gold;
        originalSilver = silv;
        originalCopper = copp;

        long totalCopper = copp + (silv * 100L) + (gold * 10000L) + (plat * 10000000L);

        if (totalCopper < finalPrice) return Results.Ok(new { ok = false, error = "The character does not have enough gold for this purchase." });

        // 3. Reserve a player listing atomically before changing live character state.
        if (source == "player")
        {
            int reserved = await database.ExecuteAsync(
                "UPDATE inventory SET Count = Count - @count " +
                "WHERE Inventory_ID = @ref AND SellPrice > 0 AND Count >= @count",
                new Dictionary<string, object> { ["@count"] = count, ["@ref"] = itemRef });
            if (reserved != 1)
                return Results.Ok(new { ok = false, error = "The listing is no longer available in the requested quantity." });
            listingReserved = true;
        }

        // 4. Apply the new balance to the live character through AldhranBridge.
        long remaining = totalCopper - finalPrice;
        long newPlat = remaining / 10000000L;
        remaining %= 10000000L;
        long newGold = remaining / 10000L;
        remaining %= 10000L;
        long newSilv = remaining / 100L;
        long newCopp = remaining % 100L;

        bool moneyUpdated = await SetMoneyAsync(bridge, buyer, newPlat, newGold, newSilv, newCopp);
        if (!moneyUpdated)
        {
            await SetMoneyAsync(bridge, buyer, plat, gold, silv, copp);
            if (listingReserved)
            {
                await database.ExecuteAsync(
                    "UPDATE inventory SET Count = Count + @count WHERE Inventory_ID = @ref",
                    new Dictionary<string, object> { ["@count"] = count, ["@ref"] = itemRef });
            }

            return Results.Ok(new { ok = false, error = "The character balance could not be updated." });
        }
        balanceChanged = true;

        // 5. Deliver through the core-neutral AldhranBridge protocol.
        JsonElement delivery = await bridge.SendAsync(new
        {
            action = "giveitem",
            name = buyer,
            item_id = itemId,
            count = count
        });

        if (!BridgeSucceeded(delivery))
        {
            logger.LogError("[SHOP] CRITICAL: gold deducted but delivery failed via giveitem — buyer={buyer} item={item}", buyer, itemId);
            bool moneyRestored = await SetMoneyAsync(bridge, buyer, plat, gold, silv, copp);
            if (listingReserved)
            {
                await database.ExecuteAsync(
                    "UPDATE inventory SET Count = Count + @count WHERE Inventory_ID = @ref",
                    new Dictionary<string, object> { ["@count"] = count, ["@ref"] = itemRef });
            }

            return Results.Ok(new
            {
                ok = false,
                gold_deducted = !moneyRestored,
                item_id = itemId,
                error = moneyRestored
                    ? "In-game delivery failed; the purchase was rolled back."
                    : "In-game delivery failed and the automatic balance rollback also failed. Contact an administrator."
            });
        }
        itemDelivered = true;

        if (listingReserved)
        {
            await database.ExecuteAsync(
                "DELETE FROM inventory WHERE Inventory_ID = @ref AND Count <= 0",
                new Dictionary<string, object> { ["@ref"] = itemRef });
        }

        logger.LogInformation("[SHOP] Purchase OK: {buyer} bought {item} x{count}", buyer, itemId, count);
        return Results.Ok(new { ok = true, gold_deducted = true });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SHOP] Exception during purchase handler for {buyer}", buyer);

        try
        {
            if (balanceChanged && !itemDelivered)
            {
                await SetMoneyAsync(
                    bridge,
                    buyer!,
                    originalPlatinum,
                    originalGold,
                    originalSilver,
                    originalCopper);
            }

            if (listingReserved && !itemDelivered)
            {
                await database.ExecuteAsync(
                    "UPDATE inventory SET Count = Count + @count WHERE Inventory_ID = @ref",
                    new Dictionary<string, object> { ["@count"] = count, ["@ref"] = itemRef! });
            }
        }
        catch (Exception rollbackException)
        {
            logger.LogError(
                rollbackException,
                "[SHOP] Automatic rollback failed for buyer={buyer} item={item}",
                buyer,
                itemRef);
        }

        return Results.Json(new { ok = false, error = "The purchase could not be completed." }, statusCode: 500);
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
        if (string.IsNullOrWhiteSpace(options.ScriptsPath))
            return Results.Json(
                new { ok = false, error = "Console:ScriptsPath is not configured." },
                statusCode: 503);

        Directory.CreateDirectory(options.ScriptsPath);

        var targetPath = Path.Combine(options.ScriptsPath, filename);
        await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);

        logger.LogInformation("[WORLD FORGE] Uploaded: {file} ({bytes} bytes)",
            filename, content.Length);

        return Results.Ok(new { ok = true, path = targetPath, bytes = content.Length });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WORLD FORGE] Upload failed: {file}", filename);
        return Results.Json(new { ok = false, error = "The file could not be uploaded." },
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
        await database.ExecuteAsync("""
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
            """);

        await database.ExecuteAsync("""
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
            """,
            new Dictionary<string, object>
            {
                ["@name"] = name,
                ["@sn"] = shortName,
                ["@tagline"] = tagline,
                ["@lore"] = loreContext,
                ["@cc"] = classCount,
                ["@status"] = status
            });

        logger.LogInformation("[WORLD FORGE] Realm synced: {name} ({status}, {cc} classes)",
            name, status, classCount);

        return Results.Ok(new { ok = true, realm = name, status });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WORLD FORGE] Realm sync failed: {name}", name);
        return Results.Json(new { ok = false, error = "The realm data could not be synchronized." },
            statusCode: 500);
    }
});

// ── GET /world-forge/realms ───────────────────────────────────
app.MapGet("/world-forge/realms", async () =>
{
    try
    {
        var rows = await database.QueryAsync(
            "SELECT id, name, short_name, tagline, class_count, status, synced_at " +
            "FROM worldforge_realms ORDER BY synced_at DESC");
        return Results.Ok(new { ok = true, realms = rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[WORLD FORGE] Realm list failed.");
        return Results.Json(new { ok = false, error = "The realm data could not be loaded." }, statusCode: 500);
    }
});

app.Run(options.ListenUrl);
