using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace CertSentinel.Core.Certificates;

/// <summary>憑證涵蓋的名稱:優先使用 SAN;沒有 SAN 擴充時才退回 Subject CN。</summary>
public sealed class CertificateNames
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    private CertificateNames(IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ipAddresses)
    {
        DnsNames = dnsNames;
        IpAddresses = ipAddresses;
    }

    public IReadOnlyList<string> DnsNames { get; }

    public IReadOnlyList<IPAddress> IpAddresses { get; }

    public IReadOnlyList<string> DisplayNames => [.. DnsNames, .. IpAddresses.Select(ip => ip.ToString())];

    public static CertificateNames Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var extension = certificate.Extensions.Cast<X509Extension>()
            .FirstOrDefault(e => e.Oid?.Value == SubjectAlternativeNameOid);
        if (extension is null)
        {
            var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return new CertificateNames(string.IsNullOrEmpty(cn) ? [] : [cn], []);
        }

        var san = extension as X509SubjectAlternativeNameExtension
            ?? new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
        return new CertificateNames([.. san.EnumerateDnsNames()], [.. san.EnumerateIPAddresses()]);
    }

    public bool Covers(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return IpAddresses.Contains(ip);
        }

        return DnsNames.Any(name => HostnameMatcher.Matches(name, host));
    }
}
