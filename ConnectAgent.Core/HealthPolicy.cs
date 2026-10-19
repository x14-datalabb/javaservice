namespace ConnectAgent.Core;

/// <summary>Event ids in the Application log (source "Sorcerer"). 3001 is the one to alarm on.</summary>
public static class EventIds
{
    public const int RunningAgain = 1001;
    public const int WaitingForDatabase = 1002;
    public const int RestNoAnswer = 1004;
    public const int ChildStarted = 1005;
    public const int RestEndpointFound = 1006;
    public const int ConnectorRestarted = 2001;
    public const int ChildRestarted = 2002;
    public const int RestPortUnknown = 2003;
    public const int FailedTooLong = 3001;
    public const int HealthCheckError = 3002;
    public const int RestartRequestFailed = 3004;
    public const int ChildExited = 3005;
}

public enum Severity { Information, Warning, Error }

public sealed record HealthEvent(int Id, Severity Severity, string Message);

public sealed record HealthOptions
{
    /// <summary>Between restarts of the same connector.</summary>
    public TimeSpan ConnectorRestartPause { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Between restarts of java.exe because the REST API stopped answering.</summary>
    public TimeSpan ChildRestartPause { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>FAILED this long gives an Error event, repeated as often.</summary>
    public TimeSpan FailedErrorAfter { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Between "waiting for the database" events per connector.</summary>
    public TimeSpan WaitingInfoEvery { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Checks in a row without an answer from the REST API before java.exe is restarted.</summary>
    public int RestFailuresBeforeChildRestart { get; init; } = 5;
}

/// <param name="RestartConnectors">Connectors to restart with restart?includeTasks=true&amp;onlyFailed=true.</param>
/// <param name="RestartChildReason">Set when java.exe should be restarted.</param>
public sealed record HealthDecision(
    IReadOnlyList<HealthEvent> Events,
    IReadOnlyList<string> RestartConnectors,
    string? RestartChildReason);

/// <summary>
/// The rules of the former PowerShell watchdog (msi-agent/watchdog), without I/O. The service
/// calls it once per health check with what it saw:
/// <list type="bullet">
/// <item>A FAILED connector or task is restarted while its database answers on TCP, at most every
/// 10 minutes per connector, without giving up: a source restart resumes from the saved offset.</item>
/// <item>While the database does not answer it is left alone, with an event every 30 minutes.</item>
/// <item>FAILED for an hour gives Error 3001, repeated hourly.</item>
/// <item>No answer from the REST API in 5 checks in a row restarts java.exe, at most every 15 minutes.</item>
/// </list>
/// </summary>
public sealed class HealthPolicy(HealthOptions options)
{
    sealed class Failing
    {
        public required DateTimeOffset Since;
        public DateTimeOffset LastRestart = DateTimeOffset.MinValue;
        public DateTimeOffset WaitingLogged = DateTimeOffset.MinValue;
        public DateTimeOffset ErrorLogged = DateTimeOffset.MinValue;
        public int Restarts;
        public string What = "";
    }

    readonly Dictionary<string, Failing> _failing = new(StringComparer.Ordinal);
    int _restFailures;
    DateTimeOffset _lastChildRestart = DateTimeOffset.MinValue;

    public HealthOptions Options { get; } = options;

    public int RestFailures => _restFailures;

    public IReadOnlyCollection<string> FailingConnectors => _failing.Keys;

    /// <summary>java.exe was (re)started: the REST API gets a fresh start.</summary>
    public void ChildStarted() => _restFailures = 0;

    public HealthDecision RestFailed(DateTimeOffset now, Uri uri, string why)
    {
        _restFailures++;
        var n = Options.RestFailuresBeforeChildRestart;
        if (_restFailures < n)
            return Only(new HealthEvent(EventIds.RestNoAnswer, Severity.Information,
                $"The REST API {uri} did not answer ({why}). java.exe is restarted after {n} checks in a row without an answer; this was {_restFailures}."));
        if (now - _lastChildRestart < Options.ChildRestartPause)
            return Only(new HealthEvent(EventIds.RestNoAnswer, Severity.Warning,
                $"The REST API {uri} has not answered in {_restFailures} checks in a row ({why}). java.exe was restarted {Minutes(now - _lastChildRestart)} min ago and is not restarted again yet."));
        var reason = $"the REST API {uri} has not answered in {_restFailures} checks in a row ({why})";
        _lastChildRestart = now;
        _restFailures = 0;
        return new HealthDecision([new HealthEvent(EventIds.ChildRestarted, Severity.Warning, $"Restarting java.exe because {reason}.")], [], reason);
    }

    /// <param name="database">The connector's database and whether it answers on TCP, or null when unknown.</param>
    public HealthDecision StatusReceived(DateTimeOffset now, IReadOnlyList<ConnectorStatus> connectors,
        Func<string, (string Endpoint, bool Answers)?> database)
    {
        _restFailures = 0;
        var events = new List<HealthEvent>();
        var restarts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in connectors)
        {
            seen.Add(c.Name);
            var parts = c.FailedParts();
            if (parts.Count == 0)
            {
                if (_failing.Remove(c.Name, out var was))
                    events.Add(new HealthEvent(EventIds.RunningAgain, Severity.Information,
                        $"{c.Name} is running again after {Minutes(now - was.Since)} min."));
                continue;
            }
            if (!_failing.TryGetValue(c.Name, out var f))
                _failing[c.Name] = f = new Failing { Since = now };
            f.What = string.Join("; ", parts);

            if (database(c.Name) is { Answers: false } db)
            {
                if (now - f.WaitingLogged >= Options.WaitingInfoEvery)
                {
                    events.Add(new HealthEvent(EventIds.WaitingForDatabase, Severity.Information,
                        $"{c.Name} is FAILED and is not restarted while its database {db.Endpoint} does not answer. {f.What}"));
                    f.WaitingLogged = now;
                }
            }
            else if (now - f.LastRestart >= Options.ConnectorRestartPause)
            {
                restarts.Add(c.Name);
                f.LastRestart = now;
            }

            if (now - f.Since >= Options.FailedErrorAfter && now - f.ErrorLogged >= Options.FailedErrorAfter)
            {
                events.Add(new HealthEvent(EventIds.FailedTooLong, Severity.Error,
                    $"{c.Name} has been FAILED for {Minutes(now - f.Since)} min despite {f.Restarts} restarts. Manual action is probably needed. {f.What}"));
                f.ErrorLogged = now;
            }
        }
        foreach (var gone in _failing.Keys.Where(k => !seen.Contains(k)).ToList())
            _failing.Remove(gone);
        return new HealthDecision(events, restarts, null);
    }

    /// <summary>The outcome of a restart request the decision asked for.</summary>
    public HealthEvent RestartSent(string name, Exception? error)
    {
        _failing.TryGetValue(name, out var f);
        var what = f?.What ?? "";
        if (error is not null)
            return new HealthEvent(EventIds.RestartRequestFailed, Severity.Error,
                $"{name} is FAILED and the restart request failed: {error.Message}. {what}");
        if (f is null)
            return new HealthEvent(EventIds.ConnectorRestarted, Severity.Warning, $"{name} was FAILED and has been restarted.");
        f.Restarts++;
        return new HealthEvent(EventIds.ConnectorRestarted, Severity.Warning,
            $"{name} was FAILED and has been restarted (restart {f.Restarts} since {f.Since.ToLocalTime():yyyy-MM-dd HH:mm}). {what}");
    }

    static HealthDecision Only(HealthEvent e) => new([e], [], null);

    static long Minutes(TimeSpan t) => (long)Math.Round(t.TotalMinutes);
}

/// <summary>
/// Wait before starting java.exe again after it exited by itself: 10 s, 30 s, 90 s, 270 s, then
/// 5 min. Back to 10 s once it has run for 10 minutes.
/// </summary>
public sealed class RestartBackoff
{
    readonly TimeSpan _first;
    readonly TimeSpan _max;
    readonly TimeSpan _stableAfter;
    TimeSpan _next;

    public RestartBackoff(TimeSpan first, TimeSpan max, TimeSpan stableAfter)
    {
        _first = _next = first;
        _max = max;
        _stableAfter = stableAfter;
    }

    public TimeSpan Next(TimeSpan ran)
    {
        if (ran >= _stableAfter)
            _next = _first;
        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(_max.Ticks, _next.Ticks * 3));
        return wait;
    }

    public void Reset() => _next = _first;
}
