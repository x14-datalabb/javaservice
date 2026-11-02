using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

/// <summary>
/// Test PKI shaped like production (msi-agent/watchdog/tests/make-pki.sh): P-384 and SHA-384,
/// mainca -> interca -> leaves with only extendedKeyUsage=clientAuth. The worker leaf is also
/// the REST server's certificate, as in Kafka Connect.
/// </summary>
sealed class TestPki : IDisposable
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Inter { get; }
    public X509Certificate2 Worker { get; }
    public X509Certificate2 Other { get; }
    public string WorkerPfx { get; }
    public const string Password = "abc=def";
    readonly string _dir;

    public TestPki()
    {
        _dir = Directory.CreateTempSubdirectory("sorcerer-pki").FullName;
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var caReq = new CertificateRequest("C=SE, O=test, OU=nv, CN=mainca.test", caKey, HashAlgorithmName.SHA384);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        Ca = caReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        using var interKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var interReq = new CertificateRequest("C=SE, O=test, OU=nv, CN=interca.test", interKey, HashAlgorithmName.SHA384);
        interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        interReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        interReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(interReq.PublicKey, false));
        interReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, true, false));
        using (var interPublic = interReq.Create(Ca, now.AddDays(-1), now.AddDays(29), Serial()))
            Inter = interPublic.CopyWithPrivateKey(interKey);

        Worker = Leaf("source_test_x14", now);
        Other = Leaf("other_test", now);
        WorkerPfx = Path.Combine(_dir, "source_test_x14.p12");
        File.WriteAllBytes(WorkerPfx, Worker.Export(X509ContentType.Pkcs12, Password));
    }

    X509Certificate2 Leaf(string id, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var req = new CertificateRequest($"C=SE, O=test, OU={id}, CN={id}", key, HashAlgorithmName.SHA384);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Inter, true, false));
        using var pub = req.Create(Inter, now.AddDays(-1), now.AddDays(28), Serial());
        // Round trip through PKCS12, so the key is usable for TLS on every platform.
        using var withKey = pub.CopyWithPrivateKey(key);
        return new X509Certificate2(withKey.Export(X509ContentType.Pkcs12, Password), Password, X509KeyStorageFlags.Exportable);
    }

    static byte[] Serial() => RandomNumberGenerator.GetBytes(16);

    public bool IssuedByUs(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Ca);
        chain.ChainPolicy.ExtraStore.Add(Inter);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;
        return chain.Build(cert);
    }

    public void Dispose()
    {
        foreach (var c in new[] { Ca, Inter, Worker, Other })
            c.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>
/// Fake Kafka Connect REST API over HTTPS with client authentication, like
/// msi-agent/watchdog/tests/fake_connect.py. GET /connectors?expand=status returns
/// <see cref="Connectors"/>; POST .../restart records the path and sets the connector and its
/// tasks to RUNNING.
/// </summary>
sealed class FakeConnect : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly X509Certificate2 _serverCert;
    readonly TestPki _pki;

    public ConcurrentDictionary<string, (string Connector, string[] Tasks)> Connectors { get; } = new();
    public ConcurrentQueue<string> Posts { get; } = new();
    public ConcurrentQueue<string> ClientSubjects { get; } = new();
    public volatile bool Hang;

    public FakeConnect(TestPki pki, X509Certificate2 serverCert)
    {
        _pki = pki;
        _serverCert = serverCert;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public Uri Uri => new($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && _pki.IssuedByUs(cert as X509Certificate2 ?? new X509Certificate2(cert)),
            }, _cts.Token);
            if (ssl.RemoteCertificate is { } rc)
                ClientSubjects.Enqueue(rc.Subject);

            using var reader = new StreamReader(ssl, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_cts.Token) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_cts.Token))) { }
            if (Hang)
                await Task.Delay(Timeout.Infinite, _cts.Token);

            var parts = requestLine.Split(' ');
            var (code, body) = parts switch
            {
                ["GET", "/connectors?expand=status", ..] => (200, StatusJson()),
                ["POST", var path, ..] => Restart(path),
                _ => (404, """{"message":"not found"}"""),
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
            await ssl.WriteAsync(bytes, _cts.Token);
            await ssl.FlushAsync(_cts.Token);
        }
        catch
        {
            // a refused handshake or a cancelled hang
        }
    }

    string StatusJson()
    {
        var o = new Dictionary<string, object>();
        foreach (var (name, (connector, tasks)) in Connectors)
            o[name] = new
            {
                status = new
                {
                    name,
                    connector = new { state = connector, worker_id = "127.0.0.1:0", trace = connector == "FAILED" ? "x\nCaused by: boom" : null },
                    tasks = tasks.Select((s, i) => new { id = i, state = s, worker_id = "127.0.0.1:0", trace = s == "FAILED" ? "x\nCaused by: boom" : null }),
                    type = "source",
                },
            };
        return JsonSerializer.Serialize(o);
    }

    (int, string) Restart(string path)
    {
        Posts.Enqueue(path);
        var name = Uri.UnescapeDataString(path.Split('/')[2].Split('?')[0]);
        if (!Connectors.TryGetValue(name, out var c))
            return (404, """{"message":"unknown connector"}""");
        Connectors[name] = ("RUNNING", c.Tasks.Select(_ => "RUNNING").ToArray());
        return (202, JsonSerializer.Serialize(new { name }));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
