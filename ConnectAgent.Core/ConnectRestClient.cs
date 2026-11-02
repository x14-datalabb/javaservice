using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConnectAgent.Core;

/// <summary>
/// Kafka Connect's REST API over HTTPS with the worker's own certificate as client certificate.
/// The server is accepted only when it presents that same certificate (the REST listener uses
/// the worker keystore), so the health check opens no new access.
/// </summary>
public sealed class ConnectRestClient : IDisposable
{
    readonly HttpClient _http;
    readonly Uri _base;
    volatile bool _pinMismatch;

    public ConnectRestClient(Uri baseUri, X509Certificate2? workerCertificate, TimeSpan timeout)
    {
        _base = baseUri;
        var handler = new HttpClientHandler { ClientCertificateOptions = ClientCertificateOption.Manual };
        if (workerCertificate is not null)
        {
            var pin = workerCertificate.GetCertHash(HashAlgorithmName.SHA256);
            handler.ClientCertificates.Add(workerCertificate);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                var ok = cert is not null && cert.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(pin);
                if (!ok)
                    _pinMismatch = true;
                return ok;
            };
        }
        _http = new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>True when the last call failed because the server did not present the worker's certificate.</summary>
    public bool PinMismatch => _pinMismatch;

    public Uri BaseUri => _base;

    public async Task<IReadOnlyList<ConnectorStatus>> GetStatusAsync(CancellationToken ct)
    {
        _pinMismatch = false;
        using var response = await _http.GetAsync(new Uri(_base, "/connectors?expand=status"), ct);
        response.EnsureSuccessStatusCode();
        return ConnectStatus.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POST /connectors/{name}/restart?includeTasks=true&amp;onlyFailed=true.</summary>
    public async Task RestartFailedAsync(string name, CancellationToken ct)
    {
        _pinMismatch = false;
        var uri = new Uri(_base, $"/connectors/{Uri.EscapeDataString(name)}/restart?includeTasks=true&onlyFailed=true");
        using var response = await _http.PostAsync(uri, null, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
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
