namespace CertSentinel.Core.Certificates;

/// <summary>
/// 憑證名稱比對(RFC 6125 子集):萬用字元只能是最左側的完整標籤,且只涵蓋單一層級。
/// <c>*.example.com</c> 涵蓋 <c>www.example.com</c>,不涵蓋 <c>a.b.example.com</c> 與 <c>example.com</c>。
/// </summary>
public static class HostnameMatcher
{
    public static bool Matches(string pattern, string host)
    {
        var p = Normalize(pattern);
        var h = Normalize(host);
        if (p.Length == 0 || h.Length == 0)
        {
            return false;
        }

        if (!p.Contains('*', StringComparison.Ordinal))
        {
            return p == h;
        }

        // 只接受「*.」開頭、其餘不含萬用字元,且至少兩個實際標籤(拒絕 *.com 這類過寬的樣式)。
        if (!p.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = p[1..]; // ".example.com"
        if (suffix.Contains('*', StringComparison.Ordinal) || suffix.Count(c => c == '.') < 2)
        {
            return false;
        }

        if (!h.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var leftmost = h[..^suffix.Length];
        return leftmost.Length > 0 && !leftmost.Contains('.', StringComparison.Ordinal);
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();
}
