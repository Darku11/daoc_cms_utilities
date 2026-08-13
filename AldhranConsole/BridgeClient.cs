/* SPDX-License-Identifier: GPL-3.0-only */
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AldhranConsole;

internal sealed class BridgeClient
{
    private readonly ConsoleOptions _options;
    private readonly ILogger<BridgeClient> _logger;

    public BridgeClient(ConsoleOptions options, ILogger<BridgeClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<JsonElement> SendAsync(object payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.BridgeTimeoutSeconds));
            using var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync(_options.BridgeHost, _options.BridgePort, timeout.Token);

            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n"
            };

            await writer.WriteLineAsync(_options.BridgeSecret);
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload));
            await writer.FlushAsync(timeout.Token);

            string? responseLine = await reader.ReadLineAsync(timeout.Token);
            string response = responseLine?.Trim() ?? "";
            if (response.Length == 0)
                return Error("The game server bridge returned no response.");

            using JsonDocument document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Bridge request to {Host}:{Port} timed out.",
                _options.BridgeHost,
                _options.BridgePort);
            return Error("The game server bridge timed out.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "The game server bridge returned invalid JSON.");
            return Error("The game server bridge returned an invalid response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Bridge request to {Host}:{Port} failed.",
                _options.BridgeHost,
                _options.BridgePort);
            return Error("The game server bridge is unavailable.");
        }
    }

    private static JsonElement Error(string message)
        => JsonSerializer.SerializeToElement(new
        {
            ok = false,
            server_online = false,
            players = Array.Empty<object>(),
            error = message
        });
}
