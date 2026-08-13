/* SPDX-License-Identifier: GPL-3.0-only */
using MySqlConnector;

namespace AldhranConsole;

internal sealed class GameDatabase
{
    private readonly string _connectionString;

    public GameDatabase(ConsoleOptions options)
    {
        _connectionString = options.DbConnection;
    }

    public async Task<List<Dictionary<string, object>>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        var rows = new List<Dictionary<string, object>>();
        await using MySqlConnection connection = await OpenConnectionAsync();
        await using var command = new MySqlCommand(sql, connection);
        AddParameters(command, parameters);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    public async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        await using MySqlConnection connection = await OpenConnectionAsync();
        await using var command = new MySqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task EnsureSchemaAsync()
    {
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS `igc_zone_points` (
                `zone_key`   VARCHAR(50) PRIMARY KEY,
                `label`      VARCHAR(100) NOT NULL,
                `region_id`  INT NOT NULL,
                `x`          INT NOT NULL,
                `y`          INT NOT NULL,
                `z`          INT NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """);

        await ExecuteAsync("""
            INSERT IGNORE INTO `igc_zone_points` (`zone_key`, `label`, `region_id`, `x`, `y`, `z`) VALUES
                ('camelot_city',  'City of Camelot (Albion)',   10,  34507, 29369, 8256),
                ('jordheim_city', 'Jordheim (Midgard)',         101, 32856, 29594, 8759),
                ('tir_na_nog',    'Tir Na Nog (Hibernia)',      201, 15972, 30919, 7060);
            """);
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void AddParameters(
        MySqlCommand command,
        IReadOnlyDictionary<string, object>? parameters)
    {
        if (parameters is null)
            return;

        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
    }
}
