using CertSentinel.Core.Notifications;
using CertSentinel.Infrastructure.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CertSentinel.Infrastructure.Email;

/// <summary>以 MailKit 寄信:支援無驗證或帳密驗證,以及 STARTTLS / 連線即 TLS。</summary>
public sealed class SmtpEmailSender(SmtpSettings settings) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var to in settings.To)
        {
            mime.To.Add(MailboxAddress.Parse(to));
        }

        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = CreateClient(settings);
        await client.ConnectAsync(settings.Host, settings.Port, ToSocketOptions(settings.Security), cancellationToken);
        if (!string.IsNullOrEmpty(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    /// <summary>
    /// MailKit 預設會在 TLS 連線時查詢 SMTP 憑證的 CRL/OCSP,等於連到 SMTP 伺服器以外的外部服務,
    /// 違反「除目標與 SMTP 外不得呼叫外部網路」,在無法對外的內網中也會讓每封信寄送失敗,因此關閉。
    /// 憑證的鏈與名稱驗證仍照常進行。
    /// </summary>
    internal static SmtpClient CreateClient(SmtpSettings settings) => new()
    {
        Timeout = settings.TimeoutSeconds * 1000,
        CheckCertificateRevocation = false,
    };

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "未知的 SMTP 加密模式。"),
    };
}
