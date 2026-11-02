using System.Security.Cryptography.X509Certificates;
using ConnectAgent.Core;
using Xunit;

/// <summary>The REST client against a real HTTPS server with client authentication.</summary>
public sealed class ConnectRestClientTests : IDisposable
{
    readonly TestPki _pki = new();
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    X509Certificate2 WorkerFromKeystore() =>
        // Loaded the way the service loads it: from the worker file's PKCS12 keystore.
        ConnectConfig.WorkerCertificate(new Dictionary<string, string>
        {
            ["ssl.keystore.location"] = Path.GetFileName(_pki.WorkerPfx),
            ["ssl.keystore.password"] = TestPki.Password,
        }, Path.GetDirectoryName(_pki.WorkerPfx)!);

    [Fact]
    public async Task ReadsStatusWithTheWorkersCertificate()
    {
        using var server = new FakeConnect(_pki, _pki.Worker);
        server.Connectors["prod_antura"] = ("RUNNING", ["FAILED"]);
        server.Connectors["prod_lime"] = ("RUNNING", ["RUNNING"]);
        using var cert = WorkerFromKeystore();
        using var client = new ConnectRestClient(server.Uri, cert, Timeout);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(2, status.Count);
        Assert.Equal("FAILED", status.Single(c => c.Name == "prod_antura").Tasks[0].State);
        Assert.Contains("CN=source_test_x14", Assert.Single(server.ClientSubjects));
        Assert.False(client.PinMismatch);
    }

    [Fact]
    public async Task RestartsOnlyFailedPartsOfAConnector()
    {
        using var server = new FakeConnect(_pki, _pki.Worker);
        server.Connectors["prod antura"] = ("RUNNING", ["FAILED"]);
        using var cert = WorkerFromKeystore();
        using var client = new ConnectRestClient(server.Uri, cert, Timeout);

        await client.RestartFailedAsync("prod antura", CancellationToken.None);

        Assert.Equal("/connectors/prod%20antura/restart?includeTasks=true&onlyFailed=true", Assert.Single(server.Posts));
        Assert.Equal("RUNNING", (await client.GetStatusAsync(CancellationToken.None))[0].Tasks[0].State);
    }

    [Fact]
    public async Task RefusesAServerWithAnotherCertificate()
    {
        // Same CA, but not the worker's own certificate: not the Connect REST listener.
        using var server = new FakeConnect(_pki, _pki.Other);
        server.Connectors["prod_antura"] = ("RUNNING", ["RUNNING"]);
        using var cert = WorkerFromKeystore();
        using var client = new ConnectRestClient(server.Uri, cert, Timeout);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.True(client.PinMismatch);
    }

    [Fact]
    public async Task ServerRefusesAClientCertificateFromAnotherCa()
    {
        using var server = new FakeConnect(_pki, _pki.Worker);
        using var stranger = new TestPki();
        // Pinned to the right server, but presenting a certificate the server does not trust.
        using var client = new ConnectRestClient(server.Uri, stranger.Worker, Timeout);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Empty(server.ClientSubjects);
    }

    [Fact]
    public async Task TimesOutWhenTheApiHangs()
    {
        using var server = new FakeConnect(_pki, _pki.Worker) { Hang = true };
        using var cert = WorkerFromKeystore();
        using var client = new ConnectRestClient(server.Uri, cert, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseCheck()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await Tcp.AnswersAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None));
        listener.Stop();
        Assert.False(await Tcp.AnswersAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    public void Dispose() => _pki.Dispose();
}

public class ConnectConfigTests
{
    [Fact]
    public void WorkerFirstThenExistingSourcesRelativeToBase()
    {
        var dir = Directory.CreateTempSubdirectory("sorcerer-cfg").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllText(Path.Combine(dir, "config", "prod_worker.properties"), "listeners=HTTPS://127.0.0.1:0\n");
            File.WriteAllText(Path.Combine(dir, "config", "antura.properties"),
                "name=prod_antura\ndatabase.hostname=agl_antura_prod\ndatabase.port=1439\n");
            File.WriteAllText(Path.Combine(dir, "config", "lime.properties"), "name=prod_lime\ndatabase.hostname=agl_limeprod \n");
            File.WriteAllText(Path.Combine(dir, "config", "connect.cfg"),
                "config\\prod_worker.properties\n\nconfig/antura.properties\nconfig/missing.properties\n  config/lime.properties  \n"
                    .Replace('\\', Path.DirectorySeparatorChar));

            var cfg = ConnectConfig.Read(Path.Combine(dir, "config", "connect.cfg"), dir);
            Assert.Equal(Path.Combine(dir, "config", "prod_worker.properties"), cfg.Worker);
            Assert.Equal(2, cfg.Sources.Count);

            var dbs = ConnectConfig.SourceDatabases(cfg.Sources);
            Assert.Equal(("agl_antura_prod", 1439), dbs["prod_antura"]);
            Assert.Equal(("agl_limeprod", 1433), dbs["prod_lime"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
