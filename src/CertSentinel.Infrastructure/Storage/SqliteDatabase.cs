using Microsoft.Data.Sqlite;

namespace CertSentinel.Infrastructure.Storage;

/// <summary>
/// 服務與 CLI 共用的 SQLite 資料庫檔。
/// 以 WAL 模式讓讀寫互不阻擋,並以 busy timeout 與 BEGIN IMMEDIATE 交易處理兩個程序同時寫入。
/// </summary>
public sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 10_000;

    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            ForeignKeys = true,
        }.ToString();
    }

    public string Path { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>建立資料夾與 Schema。可重複呼叫;服務或 CLI 任一方先啟動都會執行。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);

        // journal_mode 會持久化在資料庫檔中,且不能在交易內設定。
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);

        await using var transaction = connection.BeginTransaction(deferred: false);
        await ExecuteAsync(connection, Schema, cancellationToken, transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var versionRows = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (versionRows == 0)
        {
            await ExecuteAsync(connection, $"INSERT INTO schema_version(version) VALUES ({CurrentSchemaVersion});", cancellationToken, transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string> GetJournalModeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS targets (
            id          INTEGER PRIMARY KEY,
            host        TEXT    NOT NULL,
            port        INTEGER NOT NULL,
            name        TEXT    NOT NULL,
            enabled     INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT    NOT NULL,
            UNIQUE (host, port)
        );

        CREATE TABLE IF NOT EXISTS check_results (
            id                INTEGER PRIMARY KEY,
            target_id         INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
            checked_utc       TEXT    NOT NULL,
            source            TEXT    NOT NULL,
            status            TEXT    NOT NULL,
            days_remaining    INTEGER,
            not_before_utc    TEXT,
            not_after_utc     TEXT,
            subject           TEXT,
            issuer            TEXT,
            thumbprint_sha256 TEXT,
            san               TEXT,
            name_matches      INTEGER,
            chain_trusted     INTEGER,
            chain_errors      TEXT,
            error_kind        TEXT,
            error_message     TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_check_results_target_time ON check_results (target_id, checked_utc);
        CREATE INDEX IF NOT EXISTS ix_check_results_time ON check_results (checked_utc);

        CREATE TABLE IF NOT EXISTS notification_state (
            target_id          INTEGER PRIMARY KEY REFERENCES targets(id) ON DELETE CASCADE,
            consecutive_errors INTEGER NOT NULL DEFAULT 0,
            notified_status    TEXT,
            notified_at_utc    TEXT
        );
        """;
}
