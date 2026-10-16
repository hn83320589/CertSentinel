using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Status;

namespace CertSentinel.Cli.Output;

/// <summary>status / list / check / history 的表格與 JSON 輸出。時間以 UTC 儲存,只在這裡轉為本機時間。</summary>
internal sealed class Views(TimeZoneInfo localTimeZone)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public string TargetList(IReadOnlyList<MonitoredTarget> targets)
    {
        if (targets.Count == 0)
        {
            return "尚未加入任何監控目標。可用 certsentinel add <主機> 新增。" + Environment.NewLine;
        }

        var table = new TextTable("名稱", "主機:埠號", "啟用", "加入時間");
        foreach (var t in targets)
        {
            table.AddRow(t.Name, t.Address.ToString(), t.Enabled ? "啟用" : "停用", LocalDateTime(t.CreatedUtc));
        }

        return table.Render();
    }

    public static object TargetListJson(IReadOnlyList<MonitoredTarget> targets) =>
        targets.Select(t => new { t.Name, t.Address.Host, t.Address.Port, t.Enabled, t.CreatedUtc });

    public string Status(IReadOnlyList<TargetStatus> rows)
    {
        if (rows.Count == 0)
        {
            return "尚未加入任何監控目標。可用 certsentinel add <主機> 新增。" + Environment.NewLine;
        }

        var table = new TextTable("名稱", "主機:埠號", "狀態", "剩餘天數", "到期日", "最後檢查", "備註");
        foreach (var (target, latest) in rows)
        {
            table.AddRow(
                target.Name,
                target.Address.ToString(),
                latest?.Status.ToString() ?? "未檢查",
                latest?.DaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "-",
                latest?.Certificate is { } cert ? LocalDate(cert.NotAfterUtc) : "-",
                latest is null ? "-" : LocalDateTime(latest.CheckedUtc),
                Remarks(target.Enabled, latest));
        }

        return table.Render();
    }

    public static object StatusJson(IReadOnlyList<TargetStatus> rows) =>
        rows.Select(r => new
        {
            r.Target.Name,
            r.Target.Address.Host,
            r.Target.Address.Port,
            r.Target.Enabled,
            Status = r.Latest?.Status,
            DaysRemaining = r.Latest?.DaysRemaining,
            NotAfterUtc = r.Latest?.Certificate?.NotAfterUtc,
            LastCheckedUtc = r.Latest?.CheckedUtc,
            NameMatches = r.Latest?.Certificate?.NameMatches,
            ChainTrusted = r.Latest?.Certificate?.ChainTrusted,
            ErrorMessage = r.Latest?.ErrorMessage,
        });

    public string CheckDetail(MonitoredTarget target, CheckRecord record)
    {
        var lines = new List<(string Label, string Value)>
        {
            ("目標", $"{target.Name}({target.Address})"),
            ("狀態", record.Status.ToString()),
            ("檢查時間", LocalDateTime(record.CheckedUtc)),
        };

        if (record.Certificate is { } cert)
        {
            lines.Add(("剩餘天數", record.DaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "-"));
            lines.Add(("到期日", $"{LocalDateTime(cert.NotAfterUtc)}(UTC {cert.NotAfterUtc:yyyy-MM-dd HH:mm})"));
            lines.Add(("生效日", LocalDateTime(cert.NotBeforeUtc)));
            lines.Add(("主體", cert.Subject));
            lines.Add(("簽發者", cert.Issuer));
            lines.Add(("SHA-256 指紋", cert.ThumbprintSha256));
            lines.Add(("涵蓋名稱", string.Join(", ", cert.SubjectAlternativeNames)));
            lines.Add(("名稱比對", cert.NameMatches ? "相符" : $"名稱不符(憑證未涵蓋 {target.Address.Host})"));
            lines.Add(("鏈信任", cert.ChainTrusted ? "受系統信任" : $"不受信任:{cert.ChainErrors}"));
        }

        if (record.ErrorKind is { } kind)
        {
            lines.Add(("錯誤", $"{kind}:{record.ErrorMessage}"));
        }

        var width = lines.Max(l => TextTable.DisplayWidth(l.Label));
        return string.Concat(lines.Select(l =>
            l.Label + new string(' ', width - TextTable.DisplayWidth(l.Label)) + "  " + l.Value + Environment.NewLine));
    }

    public static object CheckJson(MonitoredTarget target, CheckRecord record) => new
    {
        target.Name,
        target.Address.Host,
        target.Address.Port,
        record.CheckedUtc,
        record.Source,
        record.Status,
        record.DaysRemaining,
        record.Certificate,
        record.ErrorKind,
        record.ErrorMessage,
    };

    public string History(IReadOnlyList<AnnotatedCheck> history)
    {
        if (history.Count == 0)
        {
            return "指定期間內沒有檢查紀錄。" + Environment.NewLine;
        }

        var table = new TextTable("檢查時間", "來源", "狀態", "剩餘天數", "到期日", "指紋", "備註");
        foreach (var (record, renewed) in history)
        {
            var remarks = new List<string>();
            if (renewed)
            {
                remarks.Add("憑證已更新");
            }

            if (record.ErrorMessage is { } error)
            {
                remarks.Add(error);
            }

            table.AddRow(
                LocalDateTime(record.CheckedUtc),
                record.Source.ToString(),
                record.Status.ToString(),
                record.DaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "-",
                record.Certificate is { } cert ? LocalDate(cert.NotAfterUtc) : "-",
                ShortThumbprint(record.Certificate?.ThumbprintSha256),
                string.Join(";", remarks));
        }

        return table.Render();
    }

    public static object HistoryJson(IReadOnlyList<AnnotatedCheck> history) =>
        history.Select(h => new
        {
            h.Record.CheckedUtc,
            h.Record.Source,
            h.Record.Status,
            h.Record.DaysRemaining,
            NotAfterUtc = h.Record.Certificate?.NotAfterUtc,
            ThumbprintSha256 = h.Record.Certificate?.ThumbprintSha256,
            CertificateRenewed = h.Renewed,
            h.Record.ErrorKind,
            h.Record.ErrorMessage,
        });

    private static string Remarks(bool enabled, CheckRecord? latest)
    {
        var remarks = new List<string>();
        if (!enabled)
        {
            remarks.Add("已停用");
        }

        if (latest?.Certificate is { } cert)
        {
            if (!cert.NameMatches)
            {
                remarks.Add("名稱不符");
            }

            if (!cert.ChainTrusted)
            {
                remarks.Add("不受信任");
            }
        }

        if (latest?.Status == CertificateStatus.Error && latest.ErrorMessage is { } error)
        {
            remarks.Add(error);
        }

        return string.Join(";", remarks);
    }

    private static string ShortThumbprint(string? thumbprint) =>
        thumbprint is null ? "-" : thumbprint.Length > 16 ? thumbprint[..16] + "…" : thumbprint;

    private string LocalDate(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, localTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string LocalDateTime(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, localTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
