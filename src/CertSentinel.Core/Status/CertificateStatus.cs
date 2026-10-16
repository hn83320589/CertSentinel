namespace CertSentinel.Core.Status;

/// <summary>目標狀態。OK 到 Expired 的數值順序即嚴重度;Error 不參與嚴重度比較。</summary>
public enum CertificateStatus
{
    OK = 0,
    Warning = 1,
    Critical = 2,
    Expired = 3,
    Error = 4,
}

public static class CertificateStatusExtensions
{
    /// <summary>比較兩個憑證狀態(OK/Warning/Critical/Expired)的嚴重度。</summary>
    public static bool IsWorseThan(this CertificateStatus status, CertificateStatus other)
    {
        if (status == CertificateStatus.Error || other == CertificateStatus.Error)
        {
            throw new ArgumentException("Error 不參與嚴重度比較。");
        }

        return status > other;
    }
}
