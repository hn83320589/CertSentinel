using CertSentinel.Core.Status;

namespace CertSentinel.Tests.Core;

public class StatusClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly StatusThresholds Thresholds = new(WarningDays: 30, CriticalDays: 7);

    private static StatusEvaluation Classify(TimeSpan remaining) =>
        StatusClassifier.Classify(Now + remaining, Now, Thresholds);

    [Fact]
    public void 剩餘31天_為OK()
    {
        var result = Classify(TimeSpan.FromDays(31));
        Assert.Equal(CertificateStatus.OK, result.Status);
        Assert.Equal(31, result.DaysRemaining);
    }

    [Fact]
    public void 剩餘31天差一秒_天數向下取整為30_為Warning()
    {
        var result = Classify(TimeSpan.FromDays(31) - TimeSpan.FromSeconds(1));
        Assert.Equal(CertificateStatus.Warning, result.Status);
        Assert.Equal(30, result.DaysRemaining);
    }

    [Fact]
    public void 剛好30天_為Warning() =>
        Assert.Equal(CertificateStatus.Warning, Classify(TimeSpan.FromDays(30)).Status);

    [Fact]
    public void 剛好8天_為Warning() =>
        Assert.Equal(CertificateStatus.Warning, Classify(TimeSpan.FromDays(8)).Status);

    [Fact]
    public void 剛好7天_為Critical() =>
        Assert.Equal(CertificateStatus.Critical, Classify(TimeSpan.FromDays(7)).Status);

    [Fact]
    public void 剩餘7天又23小時_為Critical() =>
        Assert.Equal(CertificateStatus.Critical, Classify(TimeSpan.FromDays(7) + TimeSpan.FromHours(23)).Status);

    [Fact]
    public void 剩餘不足一天_天數為0_為Critical()
    {
        var result = Classify(TimeSpan.FromHours(23));
        Assert.Equal(CertificateStatus.Critical, result.Status);
        Assert.Equal(0, result.DaysRemaining);
    }

    [Fact]
    public void 剩餘一秒_仍為Critical() =>
        Assert.Equal(CertificateStatus.Critical, Classify(TimeSpan.FromSeconds(1)).Status);

    [Fact]
    public void 剛好到期時刻_為Expired() =>
        Assert.Equal(CertificateStatus.Expired, Classify(TimeSpan.Zero).Status);

    [Fact]
    public void 已過期_為Expired_剩餘天數為負()
    {
        var result = Classify(TimeSpan.FromDays(-2.5));
        Assert.Equal(CertificateStatus.Expired, result.Status);
        Assert.Equal(-3, result.DaysRemaining);
    }

    [Fact]
    public void 門檻可自訂()
    {
        var result = StatusClassifier.Classify(Now.AddDays(14), Now, new StatusThresholds(60, 14));
        Assert.Equal(CertificateStatus.Critical, result.Status);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(7, 10)]
    [InlineData(0, -1)]
    [InlineData(-1, 5)]
    public void 不合理的門檻_丟出例外(int warning, int critical) =>
        Assert.Throws<ArgumentException>(() => new StatusThresholds(warning, critical));

    [Theory]
    [InlineData(CertificateStatus.OK, CertificateStatus.Warning, true)]
    [InlineData(CertificateStatus.Warning, CertificateStatus.Critical, true)]
    [InlineData(CertificateStatus.Critical, CertificateStatus.Expired, true)]
    [InlineData(CertificateStatus.Critical, CertificateStatus.Warning, false)]
    [InlineData(CertificateStatus.Warning, CertificateStatus.Warning, false)]
    public void 嚴重度比較(CertificateStatus from, CertificateStatus to, bool worse) =>
        Assert.Equal(worse, to.IsWorseThan(from));
}
