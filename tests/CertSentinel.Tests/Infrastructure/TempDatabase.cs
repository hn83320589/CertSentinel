using CertSentinel.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace CertSentinel.Tests.Infrastructure;

/// <summary>每個測試使用獨立的暫存 SQLite 檔,結束時刪除。</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TempDatabase(string directory, SqliteDatabase database)
    {
        _directory = directory;
        Database = database;
    }

    public SqliteDatabase Database { get; }

    public string Path => Database.Path;

    public static async Task<TempDatabase> CreateAsync()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "certsentinel-tests", Guid.NewGuid().ToString("N"));
        var database = new SqliteDatabase(System.IO.Path.Combine(directory, "test.db"));
        await database.InitializeAsync();
        return new TempDatabase(directory, database);
    }

    /// <summary>以同一個檔案建立另一個獨立的資料庫物件,模擬另一個程序或服務重啟。</summary>
    public SqliteDatabase Reopen() => new(Path);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 暫存檔偶爾仍被防毒軟體鎖住;留在 %TEMP% 不影響測試結果。
        }

        return ValueTask.CompletedTask;
    }
}
