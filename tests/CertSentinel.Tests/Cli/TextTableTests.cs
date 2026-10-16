using CertSentinel.Cli.Output;

namespace CertSentinel.Tests.Cli;

public class TextTableTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("官網", 4)]
    [InlineData("內部 LDAP", 9)]
    [InlineData("", 0)]
    public void 顯示寬度_中日韓文字算兩格(string text, int width) =>
        Assert.Equal(width, TextTable.DisplayWidth(text));

    [Fact]
    public void 欄位依顯示寬度對齊()
    {
        var table = new TextTable("名稱", "狀態");
        table.AddRow("官網", "OK");
        table.AddRow("shop", "Critical");

        var lines = table.Render().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        var statusColumn = lines.Select(l => TextTable.DisplayWidth(l[..l.IndexOfAny(['狀', 'O', 'C'])])).Distinct();
        Assert.Single(statusColumn);
    }
}
