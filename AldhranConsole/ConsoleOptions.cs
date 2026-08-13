/* SPDX-License-Identifier: GPL-3.0-only */
using Microsoft.Extensions.Configuration;

namespace AldhranConsole;

internal sealed class ConsoleOptions
{
    public string ListenUrl { get; private init; } = "http://127.0.0.1:5100";
    public string ApiSecret { get; private init; } = "";
    public string BridgeSecret { get; private init; } = "";
    public string DbConnection { get; private init; } = "";
    public string BridgeHost { get; private init; } = "127.0.0.1";
    public int BridgePort { get; private init; } = 2000;
    public int BridgeTimeoutSeconds { get; private init; } = 8;
    public string ScriptsPath { get; private init; } = "";

    public static ConsoleOptions Load(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Console");
        string sharedSecret = section["SharedSecret"]?.Trim() ?? "";

        return new ConsoleOptions
        {
            ListenUrl = ValueOrDefault(section["ListenUrl"], "http://127.0.0.1:5100"),
            ApiSecret = FirstValue(sharedSecret, section["ApiSecret"]),
            BridgeSecret = FirstValue(sharedSecret, section["BridgeSecret"]),
            DbConnection = section["DbConnection"]?.Trim() ?? "",
            BridgeHost = FirstValue(section["BridgeHost"], section["DolHost"], "127.0.0.1"),
            BridgePort = ReadInt(section["BridgePort"], section["DolPort"], 2000),
            BridgeTimeoutSeconds = ReadInt(section["BridgeTimeoutSeconds"], null, 8),
            ScriptsPath = section["ScriptsPath"]?.Trim() ?? ""
        };
    }

    public void Validate()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? listenUri)
            || (listenUri.Scheme != Uri.UriSchemeHttp && listenUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Console:ListenUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (IsMissingOrPlaceholder(ApiSecret))
            errors.Add("Console:SharedSecret (or legacy Console:ApiSecret) must be configured.");

        if (IsMissingOrPlaceholder(BridgeSecret))
            errors.Add("Console:SharedSecret (or legacy Console:BridgeSecret) must be configured.");

        if (IsMissingOrPlaceholder(DbConnection))
            errors.Add("Console:DbConnection must be configured.");

        if (string.IsNullOrWhiteSpace(BridgeHost))
            errors.Add("Console:BridgeHost must not be empty.");

        if (BridgePort is < 1 or > 65535)
            errors.Add("Console:BridgePort must be between 1 and 65535.");

        if (BridgeTimeoutSeconds is < 1 or > 120)
            errors.Add("Console:BridgeTimeoutSeconds must be between 1 and 120.");

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private static bool IsMissingOrPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string normalized = value.Trim().ToUpperInvariant();
        return normalized.Contains("CHANGE_ME", StringComparison.Ordinal)
            || normalized.Contains("REPLACE_ME", StringComparison.Ordinal)
            || normalized.Contains("YOUR_", StringComparison.Ordinal);
    }

    private static int ReadInt(string? preferred, string? fallback, int defaultValue)
    {
        string raw = FirstValue(preferred, fallback);
        return int.TryParse(raw, out int value) ? value : defaultValue;
    }

    private static string ValueOrDefault(string? value, string defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

    private static string FirstValue(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
