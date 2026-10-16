using CertSentinel.Core.Certificates;

namespace CertSentinel.Tests.Core;

public class HostnameMatcherTests
{
    [Theory]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "wwwexample.com", false)]
    [InlineData("*.example.com", "www.example.org", false)]
    [InlineData("*.EXAMPLE.com", "WWW.example.COM", true)]
    [InlineData("www.example.com", "www.example.com", true)]
    [InlineData("www.example.com.", "www.example.com", true)]
    [InlineData("www.example.com", "example.com", false)]
    [InlineData("w*.example.com", "www.example.com", false)]
    [InlineData("www.*.com", "www.example.com", false)]
    [InlineData("*", "localhost", false)]
    [InlineData("*.com", "example.com", false)]
    [InlineData("", "example.com", false)]
    public void 萬用字元只涵蓋最左側單一層級(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, HostnameMatcher.Matches(pattern, host));
    }
}
