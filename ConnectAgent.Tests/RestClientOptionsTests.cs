using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ConnectAgent.Core;
using Xunit;

/// <summary>
/// What the REST client's handler is set up with: nothing that can reach beyond 127.0.0.1 from
/// a host without internet, and the worker's certificate both ways.
/// </summary>
public sealed class RestClientOptionsTests : IDisposable
{
    readonly TestPki _pki = new();
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void ClientCertificateIsAContextForTheWorkersCertificate()
    {
        using var handler = ConnectRestClient.CreateHandler(_pki.Worker, Timeout);
        var ssl = handler.SslOptions;

        Assert.Equal(_pki.Worker.Thumbprint, ssl.ClientCertificateContext!.TargetCertificate.Thumbprint);
        // The keystore holds only the leaf; built offline, the context does not go looking for issuers.
        Assert.Empty(ssl.ClientCertificateContext.IntermediateCertificates);
        Assert.Null(ssl.ClientCertificates);
        Assert.Null(ssl.LocalCertificateSelectionCallback);
    }

    [Fact]
    public void ServerChainIsBuiltOfflineWithoutRevocationChecks()
    {
        using var handler = ConnectRestClient.CreateHandler(_pki.Worker, Timeout);
        var policy = handler.SslOptions.CertificateChainPolicy!;

        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(X509RevocationMode.NoCheck, handler.SslOptions.CertificateRevocationCheckMode);
    }

    [Fact]
    public void NoProxyNoRedirectsAndBoundedConnects()
    {
        using var handler = ConnectRestClient.CreateHandler(_pki.Worker, Timeout);

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(Timeout, handler.ConnectTimeout);
        // Shorter than the 60 s between health checks, so no connection is kept to a stopped child.
        Assert.True(handler.PooledConnectionIdleTimeout < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void AcceptsOnlyTheWorkersOwnCertificate()
    {
        var mismatches = 0;
        using var handler = ConnectRestClient.CreateHandler(_pki.Worker, Timeout, () => mismatches++);
        var check = handler.SslOptions.RemoteCertificateValidationCallback!;
        using var sameAsServerSendsIt = new X509Certificate2(_pki.Worker.RawData);

        // The chain does not matter (a leaf with clientAuth only, issuers unknown): the pin does.
        Assert.True(check(this, sameAsServerSendsIt, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(0, mismatches);
        Assert.False(check(this, _pki.Other, null, SslPolicyErrors.None));
        Assert.False(check(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Equal(2, mismatches);
    }

    [Fact]
    public void PlainHttpHasNoTlsOptions()
    {
        using var handler = ConnectRestClient.CreateHandler(null, Timeout);

        Assert.False(handler.UseProxy);
        Assert.Null(handler.SslOptions.ClientCertificateContext);
    }

    public void Dispose() => _pki.Dispose();
}

public sealed class RestClientCacheTests : IDisposable
{
    readonly TestPki _pki = new();

    Dictionary<string, string> Worker() => new()
    {
        ["ssl.keystore.location"] = Path.GetFileName(_pki.WorkerPfx),
        ["ssl.keystore.password"] = TestPki.Password,
    };

    string Base => Path.GetDirectoryName(_pki.WorkerPfx)!;

    [Fact]
    public void SameClientWhileTheKeystoreIsUnchanged()
    {
        var cache = new RestClientCache(TimeSpan.FromSeconds(5));

        var first = cache.For(Worker(), Base, https: true);
        var second = cache.For(Worker(), Base, https: true);

        Assert.Same(first, second);
        Assert.Equal(_pki.Worker.Thumbprint, first.Certificate!.Thumbprint);
    }

    [Fact]
    public async Task NewClientForARenewedKeystoreWithoutDisposingTheOldCertificate()
    {
        var cache = new RestClientCache(TimeSpan.FromSeconds(5));
        var old = cache.For(Worker(), Base, https: true);

        File.SetLastWriteTimeUtc(_pki.WorkerPfx, DateTime.UtcNow.AddMinutes(1));
        var renewed = cache.For(Worker(), Base, https: true);

        Assert.NotSame(old, renewed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => old.GetStatusAsync(new Uri("https://127.0.0.1:1"), CancellationToken.None));
        // A handshake started by the old client may still hold its certificate.
        Assert.NotEqual(IntPtr.Zero, old.Certificate!.Handle);
        Assert.True(old.Certificate.HasPrivateKey);
    }

    [Fact]
    public void NewClientWhenThePasswordChanges()
    {
        var cache = new RestClientCache(TimeSpan.FromSeconds(5));
        var first = cache.For(Worker(), Base, https: true);

        var worker = Worker();
        worker["ssl.keystore.password"] = "wrong";

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cache.For(worker, Base, https: true));
        Assert.Same(first, cache.For(Worker(), Base, https: true));
    }

    [Fact]
    public void PlainHttpNeedsNoKeystore()
    {
        var cache = new RestClientCache(TimeSpan.FromSeconds(5));

        Assert.Null(cache.For(new Dictionary<string, string>(), Base, https: false).Certificate);
    }

    public void Dispose() => _pki.Dispose();
}
