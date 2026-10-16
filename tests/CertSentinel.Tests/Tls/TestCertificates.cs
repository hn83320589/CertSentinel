using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertSentinel.Tests.Tls;

/// <summary>
/// 測試用憑證全部在執行時以 <see cref="CertificateRequest"/> 動態產生,私鑰只存在記憶體(與暫時的金鑰容器),
/// 不會寫入 Repo。
/// </summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateRootCa(string commonName = "CertSentinel Test Root CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-365), DateTimeOffset.UtcNow.AddDays(3650));
        return MakeUsableBySchannel(ca);
    }

    /// <summary>由 <paramref name="issuer"/> 簽發的伺服器憑證。</summary>
    public static X509Certificate2 Issue(
        X509Certificate2 issuer,
        string commonName,
        IEnumerable<string> dnsNames,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = CreateServerRequest(commonName, dnsNames, key);
        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(issuer, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return MakeUsableBySchannel(withKey);
    }

    public static X509Certificate2 Issue(X509Certificate2 issuer, string commonName, params string[] dnsNames) =>
        Issue(issuer, commonName, dnsNames, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

    /// <summary>自簽伺服器憑證(不受信任)。</summary>
    public static X509Certificate2 CreateSelfSigned(string commonName, params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = CreateServerRequest(commonName, dnsNames, key);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        return MakeUsableBySchannel(cert);
    }

    private static CertificateRequest CreateServerRequest(string commonName, IEnumerable<string> dnsNames, RSA key)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var names = dnsNames.ToList();
        if (names.Count > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            names.ForEach(san.AddDnsName);
            request.CertificateExtensions.Add(san.Build());
        }

        return request;
    }

    /// <summary>
    /// Windows SChannel 無法使用 CertificateRequest 產生的暫時金鑰當伺服器憑證,
    /// 需經 PFX 匯出再匯入(金鑰不設 PersistKeySet,憑證釋放時即刪除)。
    /// </summary>
    private static X509Certificate2 MakeUsableBySchannel(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
}
