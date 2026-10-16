using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace CertSentinel.Tests.Tls;

/// <summary>在 127.0.0.1 上啟動的測試伺服器,不依賴外部網路。</summary>
internal sealed class LoopbackTlsServer : IAsyncDisposable
{
    public enum Behavior
    {
        /// <summary>正常 TLS,依 SNI 主機名稱挑選憑證。</summary>
        Tls,

        /// <summary>接受 TCP 連線但永遠不回應(用來測逾時)。</summary>
        Silent,

        /// <summary>回傳非 TLS 的垃圾資料後關閉(用來測握手失敗)。</summary>
        Garbage,
    }

    private readonly TcpListener _listener;
    private readonly Behavior _behavior;
    private readonly IReadOnlyDictionary<string, X509Certificate2> _certificatesBySni;
    private readonly X509Certificate2? _defaultCertificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;

    private LoopbackTlsServer(Behavior behavior, IReadOnlyDictionary<string, X509Certificate2> certificatesBySni, X509Certificate2? defaultCertificate)
    {
        _behavior = behavior;
        _certificatesBySni = certificatesBySni;
        _defaultCertificate = defaultCertificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LoopbackTlsServer StartTls(X509Certificate2 certificate) =>
        new(Behavior.Tls, new Dictionary<string, X509Certificate2>(), certificate);

    public static LoopbackTlsServer StartTlsWithSni(IReadOnlyDictionary<string, X509Certificate2> certificatesBySni, X509Certificate2? fallback = null) =>
        new(Behavior.Tls, new Dictionary<string, X509Certificate2>(certificatesBySni, StringComparer.OrdinalIgnoreCase), fallback);

    public static LoopbackTlsServer Start(Behavior behavior) =>
        new(behavior, new Dictionary<string, X509Certificate2>(), null);

    /// <summary>取得一個目前沒有程式在聽的埠號(連線會被拒絕)。</summary>
    public static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_clients)
        {
            _clients.ForEach(c => c.Dispose());
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 停止監聽時的預期例外。
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            lock (_clients)
            {
                _clients.Add(client);
            }

            _ = Task.Run(() => HandleAsync(client), _stop.Token);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            switch (_behavior)
            {
                case Behavior.Silent:
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    break;

                case Behavior.Garbage:
                    await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\nnot tls"u8.ToArray(), _stop.Token);
                    client.Close();
                    break;

                case Behavior.Tls:
                    await using (var ssl = new SslStream(stream, leaveInnerStreamOpen: false))
                    {
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificateSelectionCallback = (_, hostName) =>
                                    hostName is not null && _certificatesBySni.TryGetValue(hostName, out var cert) ? cert : _defaultCertificate!,
                            },
                            _stop.Token);

                        // 等待用戶端關閉連線。
                        var buffer = new byte[1];
                        while (await ssl.ReadAsync(buffer, _stop.Token) > 0)
                        {
                        }
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException or SocketException)
        {
            // 用戶端中斷或伺服器停止,測試伺服器忽略即可。
        }
    }
}
