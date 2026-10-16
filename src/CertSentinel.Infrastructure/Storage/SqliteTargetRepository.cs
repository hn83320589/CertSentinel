using CertSentinel.Core.Checks;
using CertSentinel.Core.Targets;
using Microsoft.Data.Sqlite;

namespace CertSentinel.Infrastructure.Storage;

public sealed class SqliteTargetRepository(SqliteDatabase database) : ITargetRepository
{
    internal const string TargetColumns = "t.id AS t_id, t.host AS t_host, t.port AS t_port, t.name AS t_name, t.enabled AS t_enabled, t.created_utc AS t_created_utc";

    public async Task<AddTargetResult> AddAsync(TargetAddress address, string? name, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO targets (host, port, name, enabled, created_utc)
            VALUES (@host, @port, @name, 1, @created)
            ON CONFLICT (host, port) DO NOTHING;
            """;
        insert.With("@host", address.Host)
            .With("@port", address.Port)
            .With("@name", string.IsNullOrWhiteSpace(name) ? address.ToString() : name.Trim())
            .With("@created", SqliteValues.ToDb(nowUtc));
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;

        var target = await FindAsync(connection, transaction, address, cancellationToken)
            ?? throw new InvalidOperationException($"新增目標 {address} 後讀不到資料。");
        await transaction.CommitAsync(cancellationToken);

        return new AddTargetResult(inserted ? AddTargetOutcome.Added : AddTargetOutcome.Duplicate, target);
    }

    public async Task<IReadOnlyList<MonitoredTarget>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TargetColumns} FROM targets t ORDER BY t.name, t.host, t.port;";

        var targets = new List<MonitoredTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            targets.Add(ReadTarget(reader));
        }

        return targets;
    }

    public async Task<MonitoredTarget?> FindAsync(TargetAddress address, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await FindAsync(connection, null, address, cancellationToken);
    }

    public async Task<bool> RemoveAsync(TargetAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM targets WHERE host = @host AND port = @port;";
        command.With("@host", address.Host).With("@port", address.Port);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SetEnabledAsync(TargetAddress address, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE targets SET enabled = @enabled WHERE host = @host AND port = @port;";
        command.With("@enabled", enabled ? 1 : 0).With("@host", address.Host).With("@port", address.Port);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal static MonitoredTarget ReadTarget(SqliteDataReader reader) =>
        new(
            reader.GetInt64(reader.GetOrdinal("t_id")),
            new TargetAddress(reader.GetString(reader.GetOrdinal("t_host")), reader.GetInt32(reader.GetOrdinal("t_port"))),
            reader.GetString(reader.GetOrdinal("t_name")),
            reader.GetInt64(reader.GetOrdinal("t_enabled")) != 0,
            reader.ReadTime("t_created_utc"));

    private static async Task<MonitoredTarget?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, TargetAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {TargetColumns} FROM targets t WHERE t.host = @host AND t.port = @port;";
        command.With("@host", address.Host).With("@port", address.Port);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTarget(reader) : null;
    }
}
