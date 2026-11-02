using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConnectAgent.Core;

/// <summary>
/// Kafka Connect's REST API over HTTPS with the worker's own certificate as client certificate.
/// The server is accepted only when it presents that same certificate (the REST listener uses
/// the worker keystore), so the health check opens no new access.
///
/// The CDC hosts have no internet, so nothing here goes further than the REST address: no proxy,
/// and no download of issuers, root lists or revocation lists while building a chain, neither
/// for the client certificate nor for the server's. On app10 (2026-11-02) the handshake stalled
/// for 15 s before the client credentials were ready, most likely on such a download.
/// </summary>
public sealed class ConnectRestClient : IDisposable
{
    readonly HttpClient _http;
    volatile bool _pinMismatch;

    /// <param name="workerCertificate">
    /// Must stay undisposed for as long as the client or any handshake it started may use it,
    /// see <see cref="RestClientCache"/>.
    /// </param>
    public ConnectRestClient(X509Certificate2? workerCertificate, TimeSpan timeout)
    {
        Certificate = workerCertificate;
        _http = new HttpClient(CreateHandler(workerCertificate, timeout, () => _pinMismatch = true)) { Timeout = timeout };
    }

    /// <summary>True when the last call failed because the server did not present the worker's certificate.</summary>
    public bool PinMismatch => _pinMismatch;

    public X509Certificate2? Certificate { get; }

    /// <summary>
    /// The handler behind the client. With a worker certificate: TLS with it as client
    /// certificate, its context built offline (the keystore holds only the leaf, and its issuers
    /// are not fetched), and the server accepted only when it presents the same certificate
    /// (SHA-256), with its chain built offline and without revocation checks. Connections are
    /// not kept between health checks.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(X509Certificate2? workerCertificate, TimeSpan connectTimeout, Action? pinMismatch = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = connectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        };
        if (workerCertificate is null)
            return handler;

        var pin = workerCertificate.GetCertHash(HashAlgorithmName.SHA256);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificateContext = SslStreamCertificateContext.Create(workerCertificate, null, offline: true),
            CertificateChainPolicy = OfflineChainPolicy(),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                var ok = cert is not null && cert.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(pin);
                if (!ok)
                    pinMismatch?.Invoke();
                return ok;
            },
        };
        return handler;
    }

    /// <summary>Chain building from what is on the machine only. The pin decides, not the chain.</summary>
    public static X509ChainPolicy OfflineChainPolicy() => new()
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck,
    };

    public async Task<IReadOnlyList<ConnectorStatus>> GetStatusAsync(Uri baseUri, CancellationToken ct)
    {
        _pinMismatch = false;
        using var response = await _http.GetAsync(new Uri(baseUri, "/connectors?expand=status"), ct);
        response.EnsureSuccessStatusCode();
        return ConnectStatus.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POST /connectors/{name}/restart?includeTasks=true&amp;onlyFailed=true.</summary>
    public async Task RestartFailedAsync(Uri baseUri, string name, CancellationToken ct)
    {
        _pinMismatch = false;
        var uri = new Uri(baseUri, $"/connectors/{Uri.EscapeDataString(name)}/restart?includeTasks=true&onlyFailed=true");
        using var response = await _http.PostAsync(uri, null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Disposes the HTTP client, which cancels its connections. The certificate is not disposed.</summary>
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// One REST client for the worker's keystore, kept for as long as the keystore (path, password,
/// size, time of last write) stays the same, across restarts of the child.
///
/// A worker certificate is never disposed. HttpClient.Timeout ends a request but not its TLS
/// handshake, which goes on in the background and on Windows hands the certificate's native
/// handle to SChannel. Disposing the certificate then crashed the service with an
/// AccessViolationException in AcquireCredentialsHandle (app10, 2026-11-02). A replaced client
/// is disposed, which only cancels its connections; its certificate is left to the garbage
/// collector, which frees it once no handshake refers to it.
/// </summary>
public sealed class RestClientCache(TimeSpan timeout)
{
    string? _key;
    ConnectRestClient? _client;

    /// <summary>The client for this worker file; a new one when the keystore has changed.</summary>
    public ConnectRestClient For(IReadOnlyDictionary<string, string> worker, string baseDir, bool https)
    {
        var key = https ? KeystoreKey(worker, baseDir) : "http";
        if (_client is not null && key == _key)
            return _client;
        var client = new ConnectRestClient(https ? ConnectConfig.WorkerCertificate(worker, baseDir) : null, timeout);
        _client?.Dispose();
        (_client, _key) = (client, key);
        return client;
    }

    static string KeystoreKey(IReadOnlyDictionary<string, string> worker, string baseDir)
    {
        var file = new FileInfo(ConnectConfig.KeystorePath(worker, baseDir));
        worker.TryGetValue("ssl.keystore.password", out var password);
        return $"{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}\n{password}";
    }
}

public static class Tcp
{
    /// <summary>True when something accepts a TCP connection on host:port within the timeout.</summary>
    public static async Task<bool> AnswersAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
