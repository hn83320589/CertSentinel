namespace CertSentinel.Core.Status;

public sealed record StatusThresholds
{
    public StatusThresholds(int WarningDays, int CriticalDays)
    {
        if (CriticalDays < 0 || WarningDays <= CriticalDays)
        {
            throw new ArgumentException(
                $"門檻設定不合理:需要 0 ≤ 緊急天數 < 警告天數(目前 警告={WarningDays}、緊急={CriticalDays})。");
        }

        this.WarningDays = WarningDays;
        this.CriticalDays = CriticalDays;
    }

    public int WarningDays { get; }

    public int CriticalDays { get; }
}
