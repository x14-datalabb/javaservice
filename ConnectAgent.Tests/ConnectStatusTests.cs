using ConnectAgent.Core;
using Xunit;

public class ConnectStatusTests
{
    const string Json = """
        {
          "prod_lime": {"status": {"name": "prod_lime",
            "connector": {"state": "RUNNING", "worker_id": "127.0.0.1:8083"},
            "tasks": [{"id": 0, "state": "FAILED", "worker_id": "127.0.0.1:8083",
              "trace": "org.apache.kafka.connect.errors.ConnectException: An exception occurred in the change event producer.\n\tat io.debezium...\nCaused by: com.microsoft.sqlserver.jdbc.SQLServerException: Connection reset\nCaused by: java.net.SocketException: Login failed for user 'debezium'\n\tat x"}],
            "type": "source"}},
          "prod_antura": {"status": {"name": "prod_antura",
            "connector": {"state": "RUNNING", "worker_id": "127.0.0.1:8083"},
            "tasks": [{"id": 0, "state": "RUNNING", "worker_id": "127.0.0.1:8083"}],
            "type": "source"}}
        }
        """;

    [Fact]
    public void ParsesConnectorsAndTasks()
    {
        var list = ConnectStatus.Parse(Json);
        Assert.Equal(new[] { "prod_lime", "prod_antura" }, list.Select(c => c.Name));
        Assert.Equal("RUNNING", list[0].State);
        Assert.Equal("FAILED", list[0].Tasks[0].State);
        Assert.Null(list[1].Tasks[0].Trace);
    }

    [Fact]
    public void FailedPartsUseTheInnermostCause()
    {
        var list = ConnectStatus.Parse(Json);
        Assert.Equal(new[] { "task 0: java.net.SocketException: Login failed for user 'debezium'" }, list[0].FailedParts());
        Assert.Empty(list[1].FailedParts());
    }

    [Fact]
    public void RootCauseWithoutCausedBy()
    {
        Assert.Equal("no trace", ConnectStatus.RootCause(null));
        Assert.Equal("java.lang.IllegalStateException: boom", ConnectStatus.RootCause("java.lang.IllegalStateException: boom\r\n\tat a.b"));
        Assert.Equal(300, ConnectStatus.RootCause(new string('x', 400)).Length);
    }

    [Fact]
    public void EmptyAnswerMeansNoConnectors() => Assert.Empty(ConnectStatus.Parse("{}"));
}
