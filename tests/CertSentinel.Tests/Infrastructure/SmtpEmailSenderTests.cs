using System.Net;
using System.Net.Sockets;
using System.Text;
using CertSentinel.Core.Notifications;
using CertSentinel.Infrastructure.Configuration;
using CertSentinel.Infrastructure.Email;
using CertSentinel.Tests.Tls;
using MimeKit;

namespace CertSentinel.Tests.Infrastructure;

public sealed class SmtpEmailSenderTests
{
    /// <summary>只支援最基本指令的 loopback SMTP 伺服器,收下一封信後結束。</summary>
    private sealed class MiniSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public MiniSmtpServer()
        {
            _listener.Start();
            Received = Task.Run(ReceiveOneAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<(List<string> Recipients, string Data)> Received { get; }

        public void Dispose() => _listener.Stop();

        private async Task<(List<string>, string)> ReceiveOneAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

            var recipients = new List<string>();
            var data = new StringBuilder();
            await writer.WriteLineAsync("220 localhost ESMTP test");
            while (await reader.ReadLineAsync() is { } line)
            {
                var command = line.ToUpperInvariant();
                if (command.StartsWith("EHLO", StringComparison.Ordinal) || command.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 localhost");
                }
                else if (command.StartsWith("RCPT TO:", StringComparison.Ordinal))
                {
                    recipients.Add(line[8..].Trim('<', '>', ' '));
                    await writer.WriteLineAsync("250 OK");
                }
                else if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                    {
                        data.Append(dataLine).Append("\r\n");
                    }

                    await writer.WriteLineAsync("250 OK queued");
                }
                else if (command == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }

            return (recipients, data.ToString());
        }
    }

    private static SmtpSettings Settings(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Security = SmtpSecurity.None,
        From = "certsentinel@example.test",
        To = ["ops@example.test", "boss@example.test"],
    };

    [Fact]
    public async Task 寄出的信_包含主旨內文且寄給所有收件人()
    {
        using var server = new MiniSmtpServer();
        var sender = new SmtpEmailSender(Settings(server.Port));

        await sender.SendAsync(new EmailMessage("[CertSentinel] Critical — shop.example:443 剩餘 5 天", "到期時間:2026-10-21"), CancellationToken.None);

        var (recipients, data) = await server.Received.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["ops@example.test", "boss@example.test"], recipients);
        var message = await MimeMessage.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(data)));
        Assert.Equal("[CertSentinel] Critical — shop.example:443 剩餘 5 天", message.Subject);
        Assert.Contains("2026-10-21", message.TextBody, StringComparison.Ordinal);
        Assert.Equal("certsentinel@example.test", message.From.Mailboxes.Single().Address);
    }

    [Theory]
    [InlineData(SmtpSecurity.None)]
    [InlineData(SmtpSecurity.StartTls)]
    [InlineData(SmtpSecurity.SslOnConnect)]
    public void 不檢查SMTP憑證撤銷狀態_避免連到CRL與OCSP等外部服務(SmtpSecurity security)
    {
        var settings = Settings(587);
        settings.Security = security;

        using var client = SmtpEmailSender.CreateClient(settings);

        Assert.False(client.CheckCertificateRevocation);
        Assert.Equal(30_000, client.Timeout);
    }

    [Fact]
    public async Task SMTP伺服器無法連線_丟出例外讓呼叫端判定未送出()
    {
        var sender = new SmtpEmailSender(Settings(LoopbackTlsServer.GetClosedPort()));

        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(new EmailMessage("s", "b"), CancellationToken.None));
    }
}
