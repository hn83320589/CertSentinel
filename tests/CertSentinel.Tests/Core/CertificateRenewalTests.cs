using CertSentinel.Core.Checks;
using CertSentinel.Tests.Infrastructure;

namespace CertSentinel.Tests.Core;

public class CertificateRenewalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 指紋與前一筆成功紀錄不同_標示為已更新_錯誤紀錄不參與比較()
    {
        // 由新到舊
        var history = new[]
        {
            SqliteStoreTests.Ok(1, T0.AddDays(4), "NEW"),
            SqliteStoreTests.Error(1, T0.AddDays(3)),
            SqliteStoreTests.Ok(1, T0.AddDays(2), "OLD"),
            SqliteStoreTests.Ok(1, T0.AddDays(1), "OLD"),
            SqliteStoreTests.Ok(1, T0, "OLDEST"),
        };

        var marks = CertificateRenewal.Annotate(history).Select(h => h.Renewed).ToArray();

        Assert.Equal([true, false, false, true, false], marks);
    }

    [Fact]
    public void 只有一筆或全部相同_都不標示()
    {
        Assert.False(Assert.Single(CertificateRenewal.Annotate([SqliteStoreTests.Ok(1, T0, "A")])).Renewed);
        Assert.All(CertificateRenewal.Annotate([SqliteStoreTests.Ok(1, T0.AddDays(1), "A"), SqliteStoreTests.Ok(1, T0, "A")]), h => Assert.False(h.Renewed));
    }
}
