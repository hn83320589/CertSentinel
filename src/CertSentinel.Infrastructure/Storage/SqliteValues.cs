using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CertSentinel.Infrastructure.Storage;

/// <summary>SQLite 欄位轉換。時間一律以 UTC 的 ISO 8601(round-trip)字串保存,字串排序即時間排序。</summary>
internal static class SqliteValues
{
    public static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static object ToDb(DateTimeOffset? value) => value is { } v ? ToDb(v) : DBNull.Value;

    public static object ToDb<TEnum>(TEnum? value)
        where TEnum : struct, Enum => value is { } v ? v.ToString() : DBNull.Value;

    public static object ToDb(string? value) => value is null ? DBNull.Value : value;

    public static object ToDb(int? value) => value is { } v ? v : DBNull.Value;

    public static object ToDb(bool? value) => value is { } v ? (v ? 1 : 0) : DBNull.Value;

    public static DateTimeOffset ReadTime(this SqliteDataReader reader, string column) =>
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(column)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ReadNullableTime(this SqliteDataReader reader, string column) =>
        reader.IsNull(column) ? null : reader.ReadTime(column);

    public static string? ReadNullableString(this SqliteDataReader reader, string column) =>
        reader.IsNull(column) ? null : reader.GetString(reader.GetOrdinal(column));

    public static int? ReadNullableInt(this SqliteDataReader reader, string column) =>
        reader.IsNull(column) ? null : reader.GetInt32(reader.GetOrdinal(column));

    public static bool? ReadNullableBool(this SqliteDataReader reader, string column) =>
        reader.IsNull(column) ? null : reader.GetInt64(reader.GetOrdinal(column)) != 0;

    public static TEnum ReadEnum<TEnum>(this SqliteDataReader reader, string column)
        where TEnum : struct, Enum => Enum.Parse<TEnum>(reader.GetString(reader.GetOrdinal(column)));

    public static TEnum? ReadNullableEnum<TEnum>(this SqliteDataReader reader, string column)
        where TEnum : struct, Enum => reader.IsNull(column) ? null : reader.ReadEnum<TEnum>(column);

    public static bool IsNull(this SqliteDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column));

    public static SqliteCommand With(this SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
        return command;
    }
}
