using ConnectAgent.Core;
using Xunit;

public class RestEndpointTests
{
    [Fact]
    public void JettyLineFromKafka391WithRandomPort()
    {
        // ConnectStandalone 3.9.1 with listeners=http://127.0.0.1:0 (captured 2026-10-19).
        const string line = "[2026-10-19 20:28:10,551] INFO Started http_127.0.0.10@52354202{HTTP/1.1, (http/1.1)}{127.0.0.1:34185} (org.eclipse.jetty.server.AbstractConnector:333)";
        Assert.Equal(new Uri("http://127.0.0.1:34185"), RestEndpoint.FromLogLine(line));
    }

    [Fact]
    public void JettyLineWithHttps()
    {
        const string line = "[2026-10-19 20:28:10,551] INFO Started https_127.0.0.10@1a2b3c4d{SSL, (ssl, http/1.1)}{127.0.0.1:52011} (org.eclipse.jetty.server.AbstractConnector:333)";
        Assert.Equal(new Uri("https://127.0.0.1:52011"), RestEndpoint.FromLogLine(line));
    }

    [Fact]
    public void JettyLineOnAllInterfacesMeansLocalhost()
    {
        const string line = "INFO Started https_8083@7f{SSL, (ssl, http/1.1)}{0.0.0.0:8083} (org.eclipse.jetty.server.AbstractConnector:333)";
        Assert.Equal(new Uri("https://127.0.0.1:8083"), RestEndpoint.FromLogLine(line));
    }

    [Theory]
    [InlineData("INFO Started ServerConnector@4b{HTTP/1.1, (http/1.1)}{0.0.0.0:8080}")]
    [InlineData("INFO Started o.e.j.s.ServletContextHandler@2f{/,null,AVAILABLE}")]
    [InlineData("INFO Herder started (org.apache.kafka.connect.runtime.standalone.StandaloneHerder:114)")]
    [InlineData("")]
    public void OtherLinesGiveNothing(string line) => Assert.Null(RestEndpoint.FromLogLine(line));

    [Theory]
    [InlineData("HTTPS://127.0.0.1:8083", "https://127.0.0.1:8083")]
    [InlineData("https://0.0.0.0:8083,http://127.0.0.1:8084", "https://127.0.0.1:8083")]
    [InlineData("http://:8083", "http://127.0.0.1:8083")]
    public void FixedListener(string listeners, string expected) =>
        Assert.Equal(new Uri(expected), RestEndpoint.FromListeners(listeners));

    [Theory]
    [InlineData("HTTPS://127.0.0.1:0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void NoFixedListener(string? listeners) => Assert.Null(RestEndpoint.FromListeners(listeners));
}
