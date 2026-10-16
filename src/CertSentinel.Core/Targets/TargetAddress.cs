namespace CertSentinel.Core.Targets;

/// <summary>正規化後的監控目標:小寫主機名稱(或 IP)加埠號。</summary>
public sealed record TargetAddress(string Host, int Port)
{
    public const int DefaultPort = 443;

    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
