namespace CertSentinel.Core.Checks;

public sealed record AnnotatedCheck(CheckRecord Record, bool Renewed);

public static class CertificateRenewal
{
    /// <summary>
    /// 標示「憑證已更新」:成功取得憑證、且指紋與更早一筆取得憑證的紀錄不同。
    /// 無法檢查的紀錄不參與比較。輸入與輸出皆為由新到舊。
    /// </summary>
    public static IReadOnlyList<AnnotatedCheck> Annotate(IReadOnlyList<CheckRecord> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        var result = new AnnotatedCheck[newestFirst.Count];
        string? olderThumbprint = null;
        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            var record = newestFirst[i];
            var thumbprint = record.Certificate?.ThumbprintSha256;
            var renewed = thumbprint is not null && olderThumbprint is not null
                && !string.Equals(thumbprint, olderThumbprint, StringComparison.OrdinalIgnoreCase);
            result[i] = new AnnotatedCheck(record, renewed);
            olderThumbprint = thumbprint ?? olderThumbprint;
        }

        return result;
    }
}
