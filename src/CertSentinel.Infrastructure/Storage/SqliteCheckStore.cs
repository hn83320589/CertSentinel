using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Status;
using Microsoft.Data.Sqlite;

namespace CertSentinel.Infrastructure.Storage;

public sealed class SqliteCheckStore(SqliteDatabase database) : ICheckStore
{
    private const char SanSeparator = '\n';

    private const string ResultColumns = """
        r.id, r.target_id, r.checked_utc, r.source, r.status, r.days_remaining,
        r.not_before_utc, r.not_after_utc, r.subject, r.issuer, r.thumbprint_sha256, r.san,
        r.name_matches, r.chain_trusted, r.chain_errors, r.error_kind, r.error_message
        """;

    public async Task<bool> AddResultAsync(CheckRecord record, NotificationState? notificationState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        // 以 WHERE EXISTS 處理「檢查期間目標被 CLI 刪除」的競態,避免外鍵錯誤。
        insert.CommandText = """
            INSERT INTO check_results (
                target_id, checked_utc, source, status, days_remaining,
                not_before_utc, not_after_utc, subject, issuer, thumbprint_sha256, san,
                name_matches, chain_trusted, chain_errors, error_kind, error_message)
            SELECT @target_id, @checked_utc, @source, @status, @days_remaining,
                @not_before_utc, @not_after_utc, @subject, @issuer, @thumbprint, @san,
                @name_matches, @chain_trusted, @chain_errors, @error_kind, @error_message
            WHERE EXISTS (SELECT 1 FROM targets WHERE id = @target_id);
            """;
        var cert = record.Certificate;
        insert.With("@target_id", record.TargetId)
            .With("@checked_utc", SqliteValues.ToDb(record.CheckedUtc))
            .With("@source", record.Source.ToString())
            .With("@status", record.Status.ToString())
            .With("@days_remaining", SqliteValues.ToDb(record.DaysRemaining))
            .With("@not_before_utc", SqliteValues.ToDb(cert?.NotBeforeUtc))
            .With("@not_after_utc", SqliteValues.ToDb(cert?.NotAfterUtc))
            .With("@subject", SqliteValues.ToDb(cert?.Subject))
            .With("@issuer", SqliteValues.ToDb(cert?.Issuer))
            .With("@thumbprint", SqliteValues.ToDb(cert?.ThumbprintSha256))
            .With("@san", SqliteValues.ToDb(cert is null ? null : string.Join(SanSeparator, cert.SubjectAlternativeNames)))
            .With("@name_matches", SqliteValues.ToDb(cert?.NameMatches))
            .With("@chain_trusted", SqliteValues.ToDb(cert?.ChainTrusted))
            .With("@chain_errors", SqliteValues.ToDb(cert?.ChainErrors))
            .With("@error_kind", SqliteValues.ToDb(record.ErrorKind))
            .With("@error_message", SqliteValues.ToDb(record.ErrorMessage));

        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (notificationState is not null)
        {
            await UpsertStateAsync(connection, transaction, record.TargetId, notificationState, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<NotificationState> GetNotificationStateAsync(long targetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT consecutive_errors, notified_status, notified_at_utc FROM notification_state WHERE target_id = @id;";
        command.With("@id", targetId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return NotificationState.Initial;
        }

        return new NotificationState(
            reader.GetInt32(0),
            reader.ReadNullableEnum<CertificateStatus>("notified_status"),
            reader.ReadNullableTime("notified_at_utc"));
    }

    public async Task SaveNotificationStateAsync(long targetId, NotificationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await UpsertStateAsync(connection, transaction, targetId, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CheckRecord>> GetHistoryAsync(long targetId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ResultColumns} FROM check_results r
            WHERE r.target_id = @id AND r.checked_utc >= @since
            ORDER BY r.checked_utc DESC, r.id DESC;
            """;
        command.With("@id", targetId).With("@since", SqliteValues.ToDb(sinceUtc));

        var records = new List<CheckRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<TargetStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteTargetRepository.TargetColumns}, {ResultColumns}
            FROM targets t
            LEFT JOIN check_results r ON r.id = (
                SELECT id FROM check_results WHERE target_id = t.id ORDER BY checked_utc DESC, id DESC LIMIT 1)
            ORDER BY t.name, t.host, t.port;
            """;

        var rows = new List<TargetStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var target = SqliteTargetRepository.ReadTarget(reader);
            rows.Add(new TargetStatus(target, reader.IsNull("id") ? null : ReadRecord(reader)));
        }

        return rows;
    }

    public async Task<int> PurgeHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 只動歷史表;每個目標最新一筆永遠保留,notification_state 不受影響。
        command.CommandText = """
            DELETE FROM check_results
            WHERE checked_utc < @cutoff
              AND id NOT IN (
                  SELECT (SELECT id FROM check_results latest
                          WHERE latest.target_id = t.id
                          ORDER BY latest.checked_utc DESC, latest.id DESC LIMIT 1)
                  FROM targets t);
            """;
        command.With("@cutoff", SqliteValues.ToDb(cutoffUtc));
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private static async Task UpsertStateAsync(SqliteConnection connection, SqliteTransaction transaction, long targetId, NotificationState state, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO notification_state (target_id, consecutive_errors, notified_status, notified_at_utc)
            VALUES (@id, @errors, @status, @at)
            ON CONFLICT (target_id) DO UPDATE SET
                consecutive_errors = excluded.consecutive_errors,
                notified_status    = excluded.notified_status,
                notified_at_utc    = excluded.notified_at_utc;
            """;
        command.With("@id", targetId)
            .With("@errors", state.ConsecutiveErrors)
            .With("@status", SqliteValues.ToDb(state.NotifiedStatus))
            .With("@at", SqliteValues.ToDb(state.NotifiedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CheckRecord ReadRecord(SqliteDataReader reader)
    {
        CertificateInfo? certificate = null;
        if (!reader.IsNull("not_after_utc"))
        {
            var san = reader.ReadNullableString("san");
            certificate = new CertificateInfo(
                reader.ReadTime("not_before_utc"),
                reader.ReadTime("not_after_utc"),
                reader.ReadNullableString("subject") ?? string.Empty,
                reader.ReadNullableString("issuer") ?? string.Empty,
                reader.ReadNullableString("thumbprint_sha256") ?? string.Empty,
                string.IsNullOrEmpty(san) ? [] : san.Split(SanSeparator),
                reader.ReadNullableBool("name_matches") ?? false,
                reader.ReadNullableBool("chain_trusted") ?? false,
                reader.ReadNullableString("chain_errors"));
        }

        return new CheckRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt64(reader.GetOrdinal("target_id")),
            reader.ReadTime("checked_utc"),
            reader.ReadEnum<CheckSource>("source"),
            reader.ReadEnum<CertificateStatus>("status"),
            reader.ReadNullableInt("days_remaining"),
            certificate,
            reader.ReadNullableEnum<ProbeErrorKind>("error_kind"),
            reader.ReadNullableString("error_message"));
    }
}
