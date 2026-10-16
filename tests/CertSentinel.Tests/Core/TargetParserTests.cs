using CertSentinel.Core.Targets;

namespace CertSentinel.Tests.Core;

public class TargetParserTests
{
    [Theory]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("example.com:8443", "example.com", 8443)]
    [InlineData("https://example.com:8443/path", "example.com", 8443)]
    [InlineData("https://example.com", "example.com", 443)]
    [InlineData("https://example.com/path?q=1#frag", "example.com", 443)]
    [InlineData("  Example.COM  ", "example.com", 443)]
    [InlineData("HTTPS://WWW.Example.com:443/", "www.example.com", 443)]
    [InlineData("example.com.", "example.com", 443)]
    [InlineData("127.0.0.1:8443", "127.0.0.1", 8443)]
    [InlineData("[::1]:8443", "::1", 8443)]
    [InlineData("https://[::1]/", "::1", 443)]
    [InlineData("localhost", "localhost", 443)]
    public void 合法輸入_正規化為主機加埠號(string input, string host, int port)
    {
        Assert.True(TargetParser.TryParse(input, out var target, out var error), error);
        Assert.Equal(new TargetAddress(host, port), target);
    }

    [Fact]
    public void 國際化網域_轉為_punycode()
    {
        Assert.True(TargetParser.TryParse("測試.tw", out var target, out _));
        Assert.StartsWith("xn--", target.Host, StringComparison.Ordinal);
        Assert.EndsWith(".tw", target.Host, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "空白")]
    [InlineData("   ", "空白")]
    [InlineData("http://example.com", "https")]
    [InlineData("ftp://example.com", "https")]
    [InlineData("example.com:0", "埠號")]
    [InlineData("example.com:65536", "埠號")]
    [InlineData("example.com:abc", "埠號")]
    [InlineData("example.com:", "埠號")]
    [InlineData("exa mple.com", "非法字元")]
    [InlineData("*.example.com", "萬用字元")]
    [InlineData("user@example.com", "非法字元")]
    [InlineData("https://user:pw@example.com", "帳號")]
    [InlineData("-bad.example.com", "連字號")]
    [InlineData("bad-.example.com", "連字號")]
    [InlineData("a..example.com", "空的")]
    [InlineData("example.com:443:1", "埠號")]
    public void 不合法輸入_回傳錯誤說明而不丟例外(string input, string expectedFragment)
    {
        var ok = TargetParser.TryParse(input, out _, out var error);

        Assert.False(ok);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10:8443")]
    [InlineData("127.1")]
    [InlineData("0x7f000001")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("010.0.0.1")]
    [InlineData("4294967295")]
    [InlineData("1.2.3.4.5")]
    [InlineData("https://10/")]
    public void IPv4簡寫或數字主機名稱_拒絕而不悄悄轉成其他位址(string input)
    {
        Assert.False(TargetParser.TryParse(input, out _, out var error));
        Assert.Contains("IPv4", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("1password.com", "1password.com")]
    [InlineData("123.example.com", "123.example.com")]
    public void 完整IPv4與數字開頭的主機名稱_照常接受(string input, string host)
    {
        Assert.True(TargetParser.TryParse(input, out var target, out var error), error);
        Assert.Equal(host, target.Host);
    }

    [Fact]
    public void 標籤超過63字元_拒絕()
    {
        var host = new string('a', 64) + ".com";
        Assert.False(TargetParser.TryParse(host, out _, out var error));
        Assert.Contains("63", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 主機名稱總長超過253字元_拒絕()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('a', 60), 5));
        Assert.False(TargetParser.TryParse(host, out _, out var error));
        Assert.Contains("253", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetAddress_ToString_為主機冒號埠號_IPv6加中括號()
    {
        Assert.Equal("example.com:443", new TargetAddress("example.com", 443).ToString());
        Assert.Equal("[::1]:8443", new TargetAddress("::1", 8443).ToString());
    }
}
