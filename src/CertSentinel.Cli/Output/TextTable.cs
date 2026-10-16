using System.Text;

namespace CertSentinel.Cli.Output;

/// <summary>以顯示寬度(中日韓全形字算兩格)對齊的純文字表格。</summary>
internal sealed class TextTable(params string[] headers)
{
    private const string ColumnGap = "  ";
    private readonly List<string[]> _rows = [];

    public void AddRow(params string[] cells)
    {
        if (cells.Length != headers.Length)
        {
            throw new ArgumentException($"欄位數不符:需要 {headers.Length} 欄,收到 {cells.Length} 欄。", nameof(cells));
        }

        _rows.Add(cells);
    }

    public string Render()
    {
        var widths = headers.Select((h, i) => _rows.Select(r => DisplayWidth(r[i])).Append(DisplayWidth(h)).Max()).ToArray();
        var builder = new StringBuilder();
        foreach (var row in _rows.Prepend(headers))
        {
            for (var i = 0; i < row.Length; i++)
            {
                var isLast = i == row.Length - 1;
                builder.Append(row[i]);
                if (!isLast)
                {
                    builder.Append(' ', widths[i] - DisplayWidth(row[i])).Append(ColumnGap);
                }
            }

            TrimTrailingSpaces(builder);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void TrimTrailingSpaces(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }
    }

    public static int DisplayWidth(string text) => text.Sum(c => IsWide(c) ? 2 : 1);

    private static bool IsWide(char c) =>
        c is (>= (char)0x1100 and <= (char)0x115F)     // 韓文字母
            or (>= (char)0x2E80 and <= (char)0xA4CF)   // 中日韓部首、符號、漢字
            or (>= (char)0xAC00 and <= (char)0xD7A3)   // 韓文音節
            or (>= (char)0xF900 and <= (char)0xFAFF)   // 相容漢字
            or (>= (char)0xFE30 and <= (char)0xFE4F)   // 直排標點
            or (>= (char)0xFF00 and <= (char)0xFF60)   // 全形 ASCII
            or (>= (char)0xFFE0 and <= (char)0xFFE6);  // 全形符號
}
