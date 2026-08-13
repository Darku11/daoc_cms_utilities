/* SPDX-License-Identifier: GPL-3.0-only */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.Database;
using DOL.Events;

namespace DOL.GS.Scripts
{
    // The current DOL release no longer ships Newtonsoft.Json, while OpenDAoC
    // still does. The bridge protocol only needs JSON objects, arrays and scalar
    // values, so keeping this small codec inside the script avoids an additional
    // assembly requirement and makes the same file compile on both cores.
    internal static class BridgeJson
    {
        public static string SerializeObject(object value)
        {
            var output = new StringBuilder();
            WriteValue(output, value);
            return output.ToString();
        }

        private static void WriteValue(StringBuilder output, object value)
        {
            if (value == null)
            {
                output.Append("null");
                return;
            }

            if (value is string text)
            {
                WriteString(output, text);
                return;
            }

            if (value is char character)
            {
                WriteString(output, character.ToString());
                return;
            }

            if (value is bool flag)
            {
                output.Append(flag ? "true" : "false");
                return;
            }

            Type type = value.GetType();
            if (type.IsEnum)
            {
                WriteString(output, value.ToString());
                return;
            }

            if (IsNumber(type))
            {
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IDictionary dictionary)
            {
                output.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(output, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    output.Append(':');
                    WriteValue(output, entry.Value);
                }
                output.Append('}');
                return;
            }

            if (value is IEnumerable sequence)
            {
                output.Append('[');
                bool first = true;
                foreach (object entry in sequence)
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteValue(output, entry);
                }
                output.Append(']');
                return;
            }

            output.Append('{');
            bool firstProperty = true;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                if (!firstProperty) output.Append(',');
                firstProperty = false;
                WriteString(output, property.Name);
                output.Append(':');
                WriteValue(output, property.GetValue(value));
            }
            output.Append('}');
        }

        private static bool IsNumber(Type type)
        {
            TypeCode code = Type.GetTypeCode(type);
            return code == TypeCode.Byte
                || code == TypeCode.SByte
                || code == TypeCode.Int16
                || code == TypeCode.UInt16
                || code == TypeCode.Int32
                || code == TypeCode.UInt32
                || code == TypeCode.Int64
                || code == TypeCode.UInt64
                || code == TypeCode.Single
                || code == TypeCode.Double
                || code == TypeCode.Decimal;
        }

        private static void WriteString(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            output.Append(character);
                        break;
                }
            }
            output.Append('"');
        }
    }

    internal sealed class BridgeJsonArray : List<object>
    {
        public IEnumerable<T> Values<T>()
        {
            foreach (object value in this)
            {
                if (value is T typed)
                {
                    yield return typed;
                    continue;
                }

                if (value != null)
                    yield return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            }
        }
    }

    internal sealed class BridgeJsonObject : Dictionary<string, object>
    {
        public BridgeJsonObject() : base(StringComparer.OrdinalIgnoreCase) { }

        public new object this[string key]
        {
            get
            {
                object value;
                return TryGetValue(key, out value) ? value : null;
            }
            set { base[key] = value; }
        }

        public T Value<T>(string key)
        {
            object value = this[key];
            if (value == null)
                return default(T);
            if (value is T typed)
                return typed;
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }

        public static BridgeJsonObject Parse(string json)
        {
            return new Parser(json).ParseRoot();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _position;

            public Parser(string json)
            {
                _json = json ?? throw new ArgumentNullException(nameof(json));
            }

            public BridgeJsonObject ParseRoot()
            {
                SkipWhitespace();
                BridgeJsonObject result = ParseObject();
                SkipWhitespace();
                if (_position != _json.Length)
                    throw Error("Unexpected characters after the JSON object.");
                return result;
            }

            private object ParseValue()
            {
                SkipWhitespace();
                if (_position >= _json.Length)
                    throw Error("Unexpected end of JSON input.");

                char token = _json[_position];
                if (token == '{') return ParseObject();
                if (token == '[') return ParseArray();
                if (token == '"') return ParseString();
                if (token == '-' || char.IsDigit(token)) return ParseNumber();
                if (MatchLiteral("true")) return true;
                if (MatchLiteral("false")) return false;
                if (MatchLiteral("null")) return null;
                throw Error("Unexpected JSON token.");
            }

            private BridgeJsonObject ParseObject()
            {
                Expect('{');
                var result = new BridgeJsonObject();
                SkipWhitespace();
                if (Consume('}')) return result;

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (Consume('}')) return result;
                    Expect(',');
                }
            }

            private BridgeJsonArray ParseArray()
            {
                Expect('[');
                var result = new BridgeJsonArray();
                SkipWhitespace();
                if (Consume(']')) return result;

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (Consume(']')) return result;
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                var result = new StringBuilder();
                while (_position < _json.Length)
                {
                    char character = _json[_position++];
                    if (character == '"')
                        return result.ToString();
                    if (character != '\\')
                    {
                        if (character < 0x20)
                            throw Error("Unescaped control character in JSON string.");
                        result.Append(character);
                        continue;
                    }

                    if (_position >= _json.Length)
                        throw Error("Incomplete JSON escape sequence.");

                    char escaped = _json[_position++];
                    switch (escaped)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u': result.Append(ParseUnicodeEscape()); break;
                        default: throw Error("Invalid JSON escape sequence.");
                    }
                }

                throw Error("Unterminated JSON string.");
            }

            private char ParseUnicodeEscape()
            {
                if (_position + 4 > _json.Length)
                    throw Error("Incomplete JSON unicode escape.");

                string hex = _json.Substring(_position, 4);
                _position += 4;
                int codePoint;
                if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint))
                    throw Error("Invalid JSON unicode escape.");
                return (char)codePoint;
            }

            private object ParseNumber()
            {
                int start = _position;
                if (_json[_position] == '-') _position++;
                while (_position < _json.Length && char.IsDigit(_json[_position])) _position++;

                bool floatingPoint = false;
                if (_position < _json.Length && _json[_position] == '.')
                {
                    floatingPoint = true;
                    _position++;
                    while (_position < _json.Length && char.IsDigit(_json[_position])) _position++;
                }

                if (_position < _json.Length && (_json[_position] == 'e' || _json[_position] == 'E'))
                {
                    floatingPoint = true;
                    _position++;
                    if (_position < _json.Length && (_json[_position] == '+' || _json[_position] == '-')) _position++;
                    while (_position < _json.Length && char.IsDigit(_json[_position])) _position++;
                }

                string number = _json.Substring(start, _position - start);
                if (floatingPoint)
                {
                    double decimalValue;
                    if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
                        return decimalValue;
                }
                else
                {
                    long integerValue;
                    if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out integerValue))
                        return integerValue;
                }

                throw Error("Invalid JSON number.");
            }

            private bool MatchLiteral(string value)
            {
                if (_position + value.Length > _json.Length
                    || string.Compare(_json, _position, value, 0, value.Length, StringComparison.Ordinal) != 0)
                    return false;

                _position += value.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_position < _json.Length && char.IsWhiteSpace(_json[_position])) _position++;
            }

            private bool Consume(char expected)
            {
                if (_position >= _json.Length || _json[_position] != expected)
                    return false;
                _position++;
                return true;
            }

            private void Expect(char expected)
            {
                if (!Consume(expected))
                    throw Error("Expected '" + expected + "'.");
            }

            private FormatException Error(string message)
            {
                return new FormatException(message + " Position: " + _position + ".");
            }
        }
    }

    /// <summary>
    /// Aldhran Console Bridge.
    /// Listens on BRIDGE_PORT and serves every action the AldhranConsole (the
    /// ASP.NET service, "Program.cs") sends: status, kick, privlevel,
    /// teleport, giveitem, setstats, broadcast, restart, raw, heal, revive,
    /// freeze, mute, guildchat.
    ///
    /// This script is the one and only in-game delivery path for admin/console
    /// actions (including item purchases from the CMS itemshop). Do not run an
    /// older itemshop.cs / WebShopService / WebShopPoller / WebShopDispatcher
    /// alongside it — two systems answering the same purpose will conflict.
    ///
    /// Runs unmodified on both DOL and OpenDAoC. The two forks are identical
    /// for almost everything this script touches (GameClient, GamePlayer,
    /// GameEventMgr, eChatType, ...); they only diverge on a handful of APIs
    /// (online-client lookups, the logging framework, ItemTemplate's class
    /// name, ScriptMgr.GuessCommand's signature). Those diverging calls can't
    /// be written directly in source — the compiler binds them against
    /// whichever GameServer.dll is referenced, and the two forks' method sets
    /// don't overlap there — so ServerCoreCompat below resolves them via
    /// reflection at load time instead. Everything else stays plain, direct
    /// code.
    /// </summary>
    internal static class ServerCoreCompat
    {
        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = asm.GetType(fullName, false); }
                catch { continue; }

                if (type != null)
                    return type;
            }
            return null;
        }

        // ── logging ──────────────────────────────────────────────
        // OpenDAoC dropped log4net for DOL.Logging.Logger. Resolve the shared
        // method names without runtime binder call sites so the script compiler
        // does not need a Microsoft.CSharp reference.
        public static string CoreName
            => FindType("DOL.GS.ClientService") != null ? "opendaoc" : "dol";

        public static BridgeLogger CreateLogger(Type owner)
        {
            Type loggerManager = FindType("DOL.Logging.LoggerManager");
            if (loggerManager != null)
            {
                MethodInfo create = loggerManager.GetMethod("Create", new[] { typeof(Type) });
                if (create != null)
                    return new BridgeLogger(create.Invoke(null, new object[] { owner }));
            }

            Type log4netManager = FindType("log4net.LogManager");
            if (log4netManager != null)
            {
                MethodInfo getLogger = log4netManager.GetMethod("GetLogger", new[] { typeof(Type) });
                if (getLogger != null)
                    return new BridgeLogger(getLogger.Invoke(null, new object[] { owner }));
            }

            return new BridgeLogger(null);
        }

        internal sealed class BridgeLogger
        {
            private readonly object _logger;

            public BridgeLogger(object logger)
            {
                _logger = logger;
            }

            public void Debug(string message) { Write("Debug", message); }
            public void Info(string message) { Write("Info", message); }
            public void Warn(string message) { Write("Warn", message); }
            public void Error(string message) { Write("Error", message); }

            private void Write(string level, string message)
            {
                if (_logger == null)
                    return;

                try
                {
                    MethodInfo method = _logger.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == level && m.GetParameters().Length == 1)
                        .OrderBy(m => m.GetParameters()[0].ParameterType == typeof(string) ? 0 : 1)
                        .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(string)));

                    method?.Invoke(_logger, new object[] { message });
                }
                catch
                {
                    // Logging must never stop a bridge request.
                }
            }
        }

        // ── online clients ──────────────────────────────────────
        // DOL:      WorldMgr.GetAllClients() / WorldMgr.GetClientByPlayerName(name, exact, active)
        // OpenDAoC: ClientService.Instance.GetClients() / ClientService.Instance.GetPlayerByExactName(name)
        public static List<GameClient> AllClients()
        {
            Type clientService = FindType("DOL.GS.ClientService");
            if (clientService != null)
            {
                object instance = GetStaticMember(clientService, "Instance");
                MethodInfo getClients = clientService
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetClients"
                        && !m.IsGenericMethod
                        && m.GetParameters().Length == 0);
                if (instance != null && getClients != null)
                    return ToClientList(Invoke(getClients, instance, null));
            }

            Type worldMgr = FindType("DOL.GS.WorldMgr");
            MethodInfo getAllClients = worldMgr?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetAllPlayingClients" && m.GetParameters().Length == 0)
                ?? worldMgr?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetAllClients" && m.GetParameters().Length == 0);
            if (getAllClients != null)
                return ToClientList(Invoke(getAllClients, null, null));

            throw new MissingMethodException("No compatible online-client API was found for this game-server core.");
        }

        public static GameClient FindClientByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            Type clientService = FindType("DOL.GS.ClientService");
            if (clientService != null)
            {
                object instance = GetStaticMember(clientService, "Instance");
                MethodInfo getPlayer = clientService.GetMethod("GetPlayerByExactName", new[] { typeof(string) });
                if (instance != null && getPlayer != null)
                {
                    object result = Invoke(getPlayer, instance, new object[] { name });
                    if (result is GamePlayer player)
                        return player.Client;
                    if (result is GameClient client)
                        return client;
                    return null;
                }
            }

            Type worldMgr = FindType("DOL.GS.WorldMgr");
            MethodInfo getClientByName = worldMgr?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "GetClientByPlayerName")
                .Where(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length >= 1
                        && parameters.Length <= 3
                        && parameters[0].ParameterType == typeof(string)
                        && parameters.Skip(1).All(p => p.ParameterType == typeof(bool));
                })
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (getClientByName != null)
            {
                int parameterCount = getClientByName.GetParameters().Length;
                object[] args = parameterCount == 1
                    ? new object[] { name }
                    : parameterCount == 2
                        ? new object[] { name, true }
                        : new object[] { name, true, false };
                return Invoke(getClientByName, null, args) as GameClient;
            }

            // Neither known API resolved — fall back to a manual scan.
            return AllClients().FirstOrDefault(c => c.Player != null && c.Player.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // ── item templates ──────────────────────────────────────
        // DOL: DOL.Database.ItemTemplate — OpenDAoC: DOL.Database.DbItemTemplate.
        public static object FindItemTemplateByKey(string itemId)
        {
            Type templateType = FindType("DOL.Database.DbItemTemplate") ?? FindType("DOL.Database.ItemTemplate");
            if (templateType == null)
                return null;

            object database = GameServer.Database;
            MethodInfo findGeneric = database.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "FindObjectByKey" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (findGeneric == null)
                return null;

            return Invoke(findGeneric.MakeGenericMethod(templateType), database, new object[] { itemId });
        }

        public static GameInventoryItem CreateInventoryItem(string itemId, out string itemName)
        {
            object template = FindItemTemplateByKey(itemId);
            if (template == null)
            {
                itemName = null;
                return null;
            }

            ConstructorInfo constructor = typeof(GameInventoryItem)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] parameters = c.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(template);
                });

            if (constructor == null)
                throw new MissingMethodException("No compatible GameInventoryItem template constructor was found.");

            PropertyInfo nameProperty = template.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            itemName = nameProperty?.GetValue(template)?.ToString() ?? itemId;
            return (GameInventoryItem)Invoke(constructor, new object[] { template });
        }

        private static object GetStaticMember(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property != null)
                return property.GetValue(null);

            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return field?.GetValue(null);
        }

        public static string ReadTextProperty(object target, string propertyName, string fallback)
        {
            if (target == null)
                return fallback;

            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property?.GetValue(target)?.ToString() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static List<GameClient> ToClientList(object result)
        {
            var clients = new List<GameClient>();
            if (result == null)
                return clients;

            if (!(result is IEnumerable enumerable))
                throw new InvalidCastException("The online-client API returned a non-enumerable result.");

            foreach (object entry in enumerable)
            {
                if (entry is GameClient client)
                    clients.Add(client);
            }

            return clients;
        }

        private static object Invoke(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static object Invoke(ConstructorInfo constructor, object[] args)
        {
            try
            {
                return constructor.Invoke(args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        // ── commands ─────────────────────────────────────────────
        // DOL: ScriptMgr.GuessCommand(string) — OpenDAoC: ScriptMgr.GuessCommand(string, ePrivLevel).
        public static ScriptMgr.GameCommand GuessCommand(string commandName, ePrivLevel maxPrivLevel)
        {
            MethodInfo withPrivLevel = typeof(ScriptMgr).GetMethod("GuessCommand", new[] { typeof(string), typeof(ePrivLevel) });
            if (withPrivLevel != null)
                return (ScriptMgr.GameCommand)withPrivLevel.Invoke(null, new object[] { commandName, maxPrivLevel });

            MethodInfo simple = typeof(ScriptMgr).GetMethod("GuessCommand", new[] { typeof(string) });
            return simple != null ? (ScriptMgr.GameCommand)simple.Invoke(null, new object[] { commandName }) : null;
        }

        // ── encumbrance refresh ──────────────────────────────────
        // DOL: GamePlayer.UpdateEncumberance() — OpenDAoC fixed the typo:
        // GamePlayer.UpdateEncumbrance(bool forced = false).
        public static void UpdateEncumbrance(GamePlayer player)
        {
            MethodInfo method = typeof(GamePlayer).GetMethod("UpdateEncumbrance", new[] { typeof(bool) })
                ?? typeof(GamePlayer).GetMethod("UpdateEncumberance", new[] { typeof(bool) })
                ?? typeof(GamePlayer).GetMethod("UpdateEncumbrance", Type.EmptyTypes)
                ?? typeof(GamePlayer).GetMethod("UpdateEncumberance", Type.EmptyTypes);

            if (method == null)
                return;

            object[] args = method.GetParameters().Length == 0 ? null : new object[] { true };
            method.Invoke(player, args);
        }

        // OpenDAoC exposes ForceGainExperience(long), while DOL exposes
        // GainExperience(eXPSource, long). Resolve the OpenDAoC method first;
        // the enum fallback then covers current and older DOL-style cores.
        public static void GrantExperience(GamePlayer player, long amount)
        {
            MethodInfo forceMethod = typeof(GamePlayer).GetMethod(
                "ForceGainExperience",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(long) },
                null);
            if (forceMethod != null)
            {
                forceMethod.Invoke(player, new object[] { amount });
                return;
            }

            MethodInfo method = typeof(GamePlayer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GainExperience")
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType.IsEnum
                        && parameters[1].ParameterType == typeof(long);
                });

            if (method == null)
            {
                method = typeof(GamePlayer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "GainExperience")
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] parameters = m.GetParameters();
                        return parameters.Length == 3
                            && parameters[0].ParameterType.IsEnum
                            && parameters[1].ParameterType == typeof(long)
                            && parameters[2].ParameterType == typeof(bool);
                    });
            }

            if (method == null)
                throw new MissingMethodException("No compatible GamePlayer.GainExperience overload found.");

            ParameterInfo[] argsInfo = method.GetParameters();
            object source = Enum.Parse(argsInfo[0].ParameterType, "Other");
            object[] args = argsInfo.Length == 2
                ? new object[] { source, amount }
                : new object[] { source, amount, false };
            method.Invoke(player, args);
        }

        // DOL declares eReleaseType inside GamePlayer, while OpenDAoC exposes
        // it at namespace level. Invoke Release through its actual enum type so
        // an admin revive preserves the original explicit "Normal" behavior.
        public static void ReleaseNormally(GamePlayer player)
        {
            MethodInfo method = typeof(GamePlayer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Release")
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType.IsEnum
                        && parameters[1].ParameterType == typeof(bool);
                });

            if (method == null)
                throw new MissingMethodException("No compatible GamePlayer.Release overload found.");

            object normal = Enum.Parse(method.GetParameters()[0].ParameterType, "Normal");
            method.Invoke(player, new object[] { normal, true });
        }
    }

    public class AldhranBridge
    {
        private static TcpListener _listener;
        private static bool _isRunning;

        // Must match "Console:SharedSecret" (or the legacy
        // "Console:BridgeSecret") in AldhranConsole's appsettings.json exactly.
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

        private static readonly ServerCoreCompat.BridgeLogger log = ServerCoreCompat.CreateLogger(typeof(AldhranBridge));

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
                            log.Warn($"[AldhranBridge] Invalid secret received. " +
                                     $"Length={secret.Length} (expected={BRIDGE_SECRET.Length}).");
                            return;
                        }

                        string response;
                        try
                        {
                            BridgeJsonObject cmd = BridgeJsonObject.Parse(json);
                            string action = cmd.Value<string>("action");
                            response = ProcessAction(action, cmd);
                        }
                        catch (Exception ex)
                        {
                            log.Error("[AldhranBridge] Processing error: " + ex.Message);
                            response = BridgeJson.SerializeObject(new
                            {
                                ok = false,
                                server_online = true,
                                players = new object[0],
                                error = ex.Message
                            });
                        }

                        byte[] outBytes = Encoding.UTF8.GetBytes(response + "\n");
                        await stream.WriteAsync(outBytes, 0, outBytes.Length);
                        await stream.FlushAsync();

                        // The newline completes the response frame. Sending FIN as
                        // well keeps older Console builds that read until EOF working.
                        try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] Client error: " + ex.Message);
            }
        }

        private static string ProcessAction(string action, BridgeJsonObject cmd)
        {
            switch (action)
            {
                case "ping":
                    return BridgeJson.SerializeObject(new
                    {
                        ok = true,
                        server_online = true,
                        core = ServerCoreCompat.CoreName
                    });

                case "status":
                    return Handle_Status();

                case "players_online":
                    return Handle_PlayersOnline(cmd["names"] as BridgeJsonArray);

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
                    return BridgeJson.SerializeObject(new { ok = false, error = "Unknown action: " + action });
            }
        }

        // ── status ────────────────────────────────────────────────
        private static string Handle_Status()
        {
            var players = new List<object>();
            List<GameClient> clients;

            try
            {
                clients = ServerCoreCompat.AllClients();
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] status enumeration error: " + ex.Message);
                return BridgeJson.SerializeObject(new
                {
                    ok = false,
                    server_online = true,
                    core = ServerCoreCompat.CoreName,
                    players = players,
                    error = "Online players could not be enumerated: " + ex.Message
                });
            }

            foreach (GameClient gameClient in clients)
            {
                try
                {
                    if (gameClient.Player != null)
                    {
                        GamePlayer p = gameClient.Player;
                        players.Add(new
                        {
                            Name = p.Name,
                            Level = p.Level,
                            AccountName = gameClient.Account?.Name,
                            Class = ServerCoreCompat.ReadTextProperty(p.CharacterClass, "Name", "Unknown"),
                            Region = ServerCoreCompat.ReadTextProperty(p.CurrentRegion, "Description", "Unknown"),
                            PrivLevel = gameClient.Account?.PrivLevel ?? 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("[AldhranBridge] Skipping one status entry: " + ex.Message);
                }
            }

            return BridgeJson.SerializeObject(new
            {
                ok = true,
                server_online = true,
                core = ServerCoreCompat.CoreName,
                players = players,
                server_time = DateTime.UtcNow.ToString("o")
            });
        }

        private static string Handle_PlayersOnline(BridgeJsonArray requestedNames)
        {
            if (requestedNames == null)
                return BridgeJson.SerializeObject(new
                {
                    ok = false,
                    server_online = true,
                    core = ServerCoreCompat.CoreName,
                    online = new string[0],
                    error = "names must be an array."
                });

            List<string> names = requestedNames
                .Values<string>()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count > 100)
                return BridgeJson.SerializeObject(new
                {
                    ok = false,
                    server_online = true,
                    core = ServerCoreCompat.CoreName,
                    online = new string[0],
                    error = "At most 100 character names may be checked at once."
                });

            try
            {
                var online = new List<string>();
                foreach (string name in names)
                {
                    GameClient client = ServerCoreCompat.FindClientByName(name);
                    if (client?.Player != null)
                        online.Add(client.Player.Name);
                }

                return BridgeJson.SerializeObject(new
                {
                    ok = true,
                    server_online = true,
                    core = ServerCoreCompat.CoreName,
                    online = online
                });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] player presence error: " + ex.Message);
                return BridgeJson.SerializeObject(new
                {
                    ok = false,
                    server_online = true,
                    core = ServerCoreCompat.CoreName,
                    online = new string[0],
                    error = "Player presence could not be checked: " + ex.Message
                });
            }
        }

        // ── kick ──────────────────────────────────────────────────
        private static string Handle_Kick(string name, string reason)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Out.SendMessage(reason ?? "You have been kicked from the server.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                client.Player.SaveIntoDatabase();
                // OpenDAoC moved disconnect handling from GameServer to the
                // client; GameClient.Disconnect() is public on both cores.
                client.Disconnect();
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── privlevel ─────────────────────────────────────────────
        private static string Handle_PrivLevel(string name, int level)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Account.PrivLevel = (uint)level;
                GameServer.Database.SaveObject(client.Account);
                client.Out.SendMessage($"Your privilege level has been set to {level}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── teleport ──────────────────────────────────────────────
        private static string Handle_Teleport(string name, int x, int y, int z, int region)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Player.MoveTo((ushort)region, x, y, z, client.Player.Heading);
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── giveitem ──────────────────────────────────────────────
        private static string Handle_GiveItem(string buyerName, string itemId, int count)
        {
            if (count < 1 || count > 100)
                return BridgeJson.SerializeObject(new { ok = false, error = "Count must be between 1 and 100." });

            GameClient client = ServerCoreCompat.FindClientByName(buyerName);
            GamePlayer player = client?.Player;

            if (player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                string itemName;
                GameInventoryItem item = ServerCoreCompat.CreateInventoryItem(itemId, out itemName);
                if (item == null)
                    return BridgeJson.SerializeObject(new { ok = false, error = "Item not found." });

                item.Count = count;

                if (player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item))
                {
                    // AddItem already pushes the changed slot to the client itself
                    // (both forks flush it as part of the same call), so no explicit
                    // SendInventoryItemsUpdate is needed here.
                    ServerCoreCompat.UpdateEncumbrance(player);
                    player.SaveIntoDatabase();
                    player.Out.SendMessage($"You have received {itemName} x{count}!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    return BridgeJson.SerializeObject(new { ok = true });
                }

                return BridgeJson.SerializeObject(new { ok = false, error = "Inventory is full." });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] giveitem error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── setstats ──────────────────────────────────────────────
        private static string Handle_SetStats(string name, string stat, int value)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

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
                        // XP is a gain amount; both cores intentionally expose no
                        // direct setter for the character's total XP standing.
                        ServerCoreCompat.GrantExperience(player, value);
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
                        return BridgeJson.SerializeObject(new { ok = false, error = "Unknown stat: " + stat });
                }

                player.SaveIntoDatabase();
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] setstats error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── broadcast ─────────────────────────────────────────────
        private static string Handle_Broadcast(string message, string sender)
        {
            try
            {
                foreach (GameClient gameClient in ServerCoreCompat.AllClients())
                {
                    if (gameClient.Player != null)
                        gameClient.Out.SendMessage($"[{sender}] {message}", eChatType.CT_Broadcast, eChatLoc.CL_SystemWindow);
                }
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── guildchat ─────────────────────────────────────────────
        private static string Handle_GuildChat(string guildName, string sender, string message)
        {
            if (string.IsNullOrWhiteSpace(guildName) || string.IsNullOrWhiteSpace(message))
                return BridgeJson.SerializeObject(new { ok = false, error = "Missing parameters." });

            try
            {
                int count = 0;
                string formattedMessage = $"[Discord] {sender}: {message}";

                foreach (GameClient gameClient in ServerCoreCompat.AllClients())
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
                return BridgeJson.SerializeObject(new { ok = true, recipients = count });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] guildchat error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
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

                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
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
                return BridgeJson.SerializeObject(new { ok = false, error = "No command given." });

            string trimmed = commandLine.Trim();
            if (trimmed.StartsWith("/") || trimmed.StartsWith("&"))
                trimmed = trimmed.Substring(1);

            string[] pars = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (pars.Length == 0)
                return BridgeJson.SerializeObject(new { ok = false, error = "No command given." });

            string cmdName = pars[0];

            if (BLOCKED_RAW_COMMANDS.Contains(cmdName))
                return BridgeJson.SerializeObject(new { ok = false, error = $"Command '{cmdName}' is blocked." });

            try
            {
                // Prefer an executor by name; otherwise fall back to the first
                // online GM/admin (PrivLevel >= 2), since some command handlers
                // require a GameClient with sufficient PrivLevel.
                GameClient executorClient = null;

                if (!string.IsNullOrWhiteSpace(executorName))
                    executorClient = ServerCoreCompat.FindClientByName(executorName);

                if (executorClient == null)
                {
                    foreach (GameClient gc in ServerCoreCompat.AllClients())
                    {
                        if (gc.Player != null && gc.Account != null && gc.Account.PrivLevel >= 2)
                        {
                            executorClient = gc;
                            break;
                        }
                    }
                }

                if (executorClient == null || executorClient.Player == null)
                    return BridgeJson.SerializeObject(new { ok = false, error = "No suitable executor (online GM/admin) found." });

                string serverCommand = "&" + cmdName;
                var cmdEntry = ServerCoreCompat.GuessCommand(serverCommand, (ePrivLevel)executorClient.Account.PrivLevel);
                if (cmdEntry == null)
                    return BridgeJson.SerializeObject(new { ok = false, error = $"Unknown command: {cmdName}" });

                pars[0] = cmdEntry.m_cmd;

                cmdEntry.m_cmdHandler.OnCommand(executorClient, pars);

                log.Warn($"[AldhranBridge] RAW COMMAND executed by '{executorClient.Player.Name}': {commandLine}");
                return BridgeJson.SerializeObject(new { ok = true, result = $"Command '{cmdName}' executed (executor: {executorClient.Player.Name})." });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] raw error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── heal ──────────────────────────────────────────────────
        private static string Handle_Heal(string name)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;
                player.Health = player.MaxHealth;
                player.Mana = player.MaxMana;
                player.Endurance = player.MaxEndurance;
                player.Out.SendCharStatsUpdate();
                player.UpdatePlayerStatus();
                player.Out.SendMessage("You have been fully healed by an admin.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── revive ────────────────────────────────────────────────
        private static string Handle_Revive(string name)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                GamePlayer player = client.Player;
                ServerCoreCompat.ReleaseNormally(player);
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
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
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

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
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] freeze error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }

        // ── mute / unmute ─────────────────────────────────────────
        private static string Handle_Mute(string name, bool on)
        {
            GameClient client = ServerCoreCompat.FindClientByName(name);
            if (client == null || client.Player == null)
                return BridgeJson.SerializeObject(new { ok = false, error = "Player offline." });

            try
            {
                client.Player.IsMuted = on;
                client.Account.IsMuted = on;
                GameServer.Database.SaveObject(client.Account);
                client.Player.Out.SendMessage(
                    on ? "You have been muted by an admin." : "You have been unmuted.",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                return BridgeJson.SerializeObject(new { ok = true });
            }
            catch (Exception ex)
            {
                log.Error("[AldhranBridge] mute error: " + ex.Message);
                return BridgeJson.SerializeObject(new { ok = false, error = ex.Message });
            }
        }
    }
}
