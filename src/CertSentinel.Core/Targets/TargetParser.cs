using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CertSentinel.Core.Targets;

/// <summary>
/// 將 <c>example.com</c>、<c>example.com:8443</c>、<c>https://example.com:8443/path</c>
/// 正規化為 <see cref="TargetAddress"/>。不合法時回傳可直接顯示給使用者的錯誤訊息。
/// </summary>
public static class TargetParser
{
    private const int MaxLabelLength = 63;
    private const int MaxHostLength = 253;

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    public static bool TryParse(
        string? input,
        [NotNullWhen(true)] out TargetAddress? target,
        [NotNullWhen(false)] out string? error)
    {
        target = null;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "目標不可為空白。";
            return false;
        }

        if (!TryExtractAuthority(text, out var authority, out error)
            || !TrySplitHostPort(authority, out var rawHost, out var port, out error)
            || !TryNormalizeHost(rawHost, out var host, out error))
        {
            return false;
        }

        target = new TargetAddress(host, port);
        return true;
    }

    private static bool TryExtractAuthority(string text, out string authority, [NotNullWhen(false)] out string? error)
    {
        authority = text;
        error = null;
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return true;
        }

        var scheme = text[..schemeEnd];
        if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            error = $"不支援的 scheme「{scheme}」:僅支援 https:// 或不帶 scheme 的主機名稱。";
            return false;
        }

        var rest = text[(schemeEnd + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        authority = end < 0 ? rest : rest[..end];
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            error = "網址不可包含帳號或密碼。";
            return false;
        }

        return true;
    }

    private static bool TrySplitHostPort(string authority, out string host, out int port, [NotNullWhen(false)] out string? error)
    {
        host = authority;
        port = TargetAddress.DefaultPort;
        error = null;
        string? portText = null;

        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                error = "IPv6 位址缺少結尾的「]」。";
                return false;
            }

            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    error = "IPv6 位址後只能接「:埠號」。";
                    return false;
                }

                portText = rest[1..];
            }

            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = $"「{host}」不是合法的 IPv6 位址。";
                return false;
            }
        }
        else
        {
            var colonCount = authority.Count(c => c == ':');
            if (colonCount == 1)
            {
                var colon = authority.IndexOf(':', StringComparison.Ordinal);
                host = authority[..colon];
                portText = authority[(colon + 1)..];
            }
            else if (colonCount > 1 && !IPAddress.TryParse(authority, out _))
            {
                error = "無法辨識埠號(IPv6 位址請以 [ ] 包住,例如 [::1]:443)。";
                return false;
            }
        }

        if (portText is not null
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535))
        {
            error = $"埠號「{portText}」不合法:埠號必須是 1–65535 的整數。";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeHost(string rawHost, out string host, [NotNullWhen(false)] out string? error)
    {
        host = string.Empty;
        error = null;

        if (IPAddress.TryParse(rawHost, out var ip))
        {
            host = ip.ToString();
            return true;
        }

        var name = rawHost.TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0)
        {
            error = "主機名稱不可為空白。";
            return false;
        }

        if (name.Contains('*', StringComparison.Ordinal))
        {
            error = "不可使用萬用字元,請輸入實際要檢查的主機名稱。";
            return false;
        }

        var invalid = name.FirstOrDefault(c => char.IsWhiteSpace(c) || (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'));
        if (invalid != default)
        {
            error = $"主機名稱含非法字元「{invalid}」。";
            return false;
        }

        if (!TryValidateLabels(name, out error))
        {
            return false;
        }

        try
        {
            host = Idn.GetAscii(name);
        }
        catch (ArgumentException ex)
        {
            error = $"主機名稱不合法:{ex.Message}";
            return false;
        }

        return TryValidateLabels(host, out error);
    }

    private static bool TryValidateLabels(string name, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (name.Length > MaxHostLength)
        {
            error = $"主機名稱最多 {MaxHostLength} 字元。";
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0)
            {
                error = "主機名稱含空的標籤(連續的「.」)。";
                return false;
            }

            if (label.Length > MaxLabelLength)
            {
                error = $"主機名稱的每個標籤最多 {MaxLabelLength} 字元。";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                error = "主機名稱的標籤不可以連字號開頭或結尾。";
                return false;
            }
        }

        return true;
    }
}
