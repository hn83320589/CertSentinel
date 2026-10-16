namespace CertSentinel.Core.Notifications;

public sealed record EmailMessage(string Subject, string Body);

public interface IEmailSender
{
    /// <summary>寄出通知給設定的收件人清單。失敗時丟出例外,呼叫端據此判定「未送出」。</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
