using ConnectAgent.Core;
using Xunit;

public class HealthPolicyTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 20, 0, 15, 0, TimeSpan.Zero);
    static readonly Uri Rest = new("https://127.0.0.1:8083");

    static ConnectorStatus Running(string name) => new(name, "RUNNING", null, [new ConnectTaskStatus(0, "RUNNING", null)]);

    static ConnectorStatus TaskFailed(string name) =>
        new(name, "RUNNING", null, [new ConnectTaskStatus(0, "FAILED", "x\nCaused by: java.net.SocketTimeoutException: Connect timed out")]);

    static Func<string, (string Endpoint, bool Answers)?> Db(bool answers) => _ => ("db:1439", answers);

    static readonly Func<string, (string Endpoint, bool Answers)?> UnknownDb = _ => null;

    [Fact]
    public void FailedTaskIsRestartedWhileItsDatabaseAnswers()
    {
        var p = new HealthPolicy(new HealthOptions());
        var d = p.StatusReceived(T0, [TaskFailed("prod_antura"), Running("prod_lime")], Db(true));
        Assert.Equal(new[] { "prod_antura" }, d.RestartConnectors);
        Assert.Null(d.RestartChildReason);

        var e = p.RestartSent("prod_antura", null);
        Assert.Equal(EventIds.ConnectorRestarted, e.Id);
        Assert.Equal(Severity.Warning, e.Severity);
        Assert.Contains("restart 1 since", e.Message);
        Assert.Contains("Connect timed out", e.Message);
    }

    [Fact]
    public void FailedConnectorItselfCountsToo()
    {
        var p = new HealthPolicy(new HealthOptions());
        var d = p.StatusReceived(T0, [new ConnectorStatus("prod_lime", "FAILED", "boom", [])], Db(true));
        Assert.Equal(new[] { "prod_lime" }, d.RestartConnectors);
    }

    [Fact]
    public void RestartedAtMostEveryTenMinutesAndNeverGivesUp()
    {
        var p = new HealthPolicy(new HealthOptions());
        var restarts = 0;
        for (var minute = 0; minute <= 120; minute++)
        {
            var d = p.StatusReceived(T0.AddMinutes(minute), [TaskFailed("prod_antura")], Db(true));
            restarts += d.RestartConnectors.Count;
            foreach (var name in d.RestartConnectors)
                p.RestartSent(name, null);
        }
        Assert.Equal(13, restarts); // minute 0, 10, ..., 120
    }

    [Fact]
    public void WaitsWhileTheDatabaseDoesNotAnswer()
    {
        var p = new HealthPolicy(new HealthOptions());
        var events = new List<HealthEvent>();
        for (var minute = 0; minute < 60; minute++)
        {
            var d = p.StatusReceived(T0.AddMinutes(minute), [TaskFailed("prod_antura")], Db(false));
            Assert.Empty(d.RestartConnectors);
            events.AddRange(d.Events);
        }
        var waiting = events.Where(e => e.Id == EventIds.WaitingForDatabase).ToList();
        Assert.Equal(2, waiting.Count); // minute 0 and 30
        Assert.Contains("db:1439 does not answer", waiting[0].Message);

        // The database is back: restarted right away.
        var back = p.StatusReceived(T0.AddMinutes(60), [TaskFailed("prod_antura")], Db(true));
        Assert.Equal(new[] { "prod_antura" }, back.RestartConnectors);
    }

    [Fact]
    public void UnknownDatabaseIsNotWaitedFor()
    {
        var p = new HealthPolicy(new HealthOptions());
        Assert.Equal(new[] { "x" }, p.StatusReceived(T0, [TaskFailed("x")], UnknownDb).RestartConnectors);
    }

    [Fact]
    public void ErrorAfterAnHourRepeatedHourly()
    {
        var p = new HealthPolicy(new HealthOptions());
        var errors = new List<HealthEvent>();
        for (var minute = 0; minute <= 150; minute++)
            errors.AddRange(p.StatusReceived(T0.AddMinutes(minute), [TaskFailed("prod_antura")], Db(false)).Events
                .Where(e => e.Id == EventIds.FailedTooLong));
        Assert.Equal(2, errors.Count); // minute 60 and 120
        Assert.All(errors, e => Assert.Equal(Severity.Error, e.Severity));
        Assert.Contains("FAILED for 60 min", errors[0].Message);
    }

    [Fact]
    public void RunningAgainIsReportedOnce()
    {
        var p = new HealthPolicy(new HealthOptions());
        p.StatusReceived(T0, [TaskFailed("prod_antura")], Db(true));
        var d = p.StatusReceived(T0.AddMinutes(12), [Running("prod_antura")], Db(true));
        var e = Assert.Single(d.Events);
        Assert.Equal(EventIds.RunningAgain, e.Id);
        Assert.Contains("after 12 min", e.Message);
        Assert.Empty(p.StatusReceived(T0.AddMinutes(13), [Running("prod_antura")], Db(true)).Events);
        Assert.Empty(p.FailingConnectors);
    }

    [Fact]
    public void ConnectorsThatDisappearAreForgotten()
    {
        var p = new HealthPolicy(new HealthOptions());
        p.StatusReceived(T0, [TaskFailed("old")], Db(true));
        p.StatusReceived(T0.AddMinutes(1), [Running("new")], Db(true));
        Assert.Empty(p.FailingConnectors);
    }

    [Fact]
    public void FailedRestartRequestIsAnErrorAndWaitsForThePause()
    {
        var p = new HealthPolicy(new HealthOptions());
        var d = p.StatusReceived(T0, [TaskFailed("prod_antura")], Db(true));
        var e = p.RestartSent(d.RestartConnectors[0], new HttpRequestException("500 Internal Server Error"));
        Assert.Equal(EventIds.RestartRequestFailed, e.Id);
        Assert.Equal(Severity.Error, e.Severity);
        Assert.Empty(p.StatusReceived(T0.AddMinutes(5), [TaskFailed("prod_antura")], Db(true)).RestartConnectors);
        Assert.Single(p.StatusReceived(T0.AddMinutes(10), [TaskFailed("prod_antura")], Db(true)).RestartConnectors);
    }

    [Fact]
    public void RestApiSilentFiveChecksInARowRestartsTheChild()
    {
        var p = new HealthPolicy(new HealthOptions());
        for (var i = 0; i < 4; i++)
        {
            var d = p.RestFailed(T0.AddMinutes(i), Rest, "timeout");
            Assert.Null(d.RestartChildReason);
            Assert.Equal(EventIds.RestNoAnswer, Assert.Single(d.Events).Id);
        }
        var fifth = p.RestFailed(T0.AddMinutes(4), Rest, "timeout");
        Assert.NotNull(fifth.RestartChildReason);
        Assert.Equal(EventIds.ChildRestarted, Assert.Single(fifth.Events).Id);
        Assert.Equal(0, p.RestFailures);
    }

    [Fact]
    public void ChildIsNotRestartedAgainWithinFifteenMinutes()
    {
        var p = new HealthPolicy(new HealthOptions());
        for (var i = 0; i < 5; i++)
            p.RestFailed(T0.AddMinutes(i), Rest, "timeout");
        p.ChildStarted();
        string? reason = null;
        var minute = 5;
        for (; reason is null && minute < 60; minute++)
            reason = p.RestFailed(T0.AddMinutes(minute), Rest, "timeout").RestartChildReason;
        Assert.Equal(19, minute - 1); // 15 min after the restart at minute 4
    }

    [Fact]
    public void AnAnswerResetsTheCount()
    {
        var p = new HealthPolicy(new HealthOptions());
        for (var i = 0; i < 4; i++)
            p.RestFailed(T0.AddMinutes(i), Rest, "timeout");
        p.StatusReceived(T0.AddMinutes(4), [Running("prod_antura")], Db(true));
        Assert.Equal(0, p.RestFailures);
        Assert.Null(p.RestFailed(T0.AddMinutes(5), Rest, "timeout").RestartChildReason);
    }
}

public class RestartBackoffTests
{
    [Fact]
    public void GrowsToFiveMinutesAndResetsAfterAStableRun()
    {
        var b = new RestartBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var quick = TimeSpan.FromSeconds(5);
        Assert.Equal(new[] { 10, 30, 90, 270, 300, 300 }, Enumerable.Range(0, 6).Select(_ => (int)b.Next(quick).TotalSeconds));
        Assert.Equal(10, (int)b.Next(TimeSpan.FromMinutes(11)).TotalSeconds);
        Assert.Equal(30, (int)b.Next(quick).TotalSeconds);
        b.Reset();
        Assert.Equal(10, (int)b.Next(quick).TotalSeconds);
    }
}
