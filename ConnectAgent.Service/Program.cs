// .NET 8 Windows Service "Sorcerer" that supervises a child process: Kafka Connect for the X14
// CDC agent. Set X14_CHILD_EXE / X14_CHILD_ARGS / X14_CHILD_CWD in the service's registry
// Environment; without X14_CHILD_EXE it idles.
//
// The child is started again when it exits by itself, and a health check restarts FAILED
// connectors through Kafka Connect's REST API (README.md). This replaces the separate
// PowerShell watchdog (msi-agent/watchdog).

using System.Collections.Generic;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ConnectAgent.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using System.Linq;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "Sorcerer")
    .ConfigureLogging((ctx, logging) =>
    {
        logging.ClearProviders();
        logging.AddEventLog(cfg =>
        {
            cfg.SourceName = "Sorcerer";
            cfg.LogName = "Application";
        });
        // The host only sends Warning and above to the event log. The supervisor's own
        // Information events (1001 running again, 1002 waiting for the database, 1004, 1005,
        // 1006) belong there too. The child's stdout ("Sorcerer.Child") stays out of it.
        logging.AddFilter<EventLogLoggerProvider>("Worker", LogLevel.Information);
        logging.AddConsole();
    })
    .ConfigureServices(services => services.AddHostedService<Worker>());

await builder.Build().RunAsync();

sealed class Worker(ILogger<Worker> log, ILoggerFactory loggers) : BackgroundService
{
    const string ServiceName = "Sorcerer"; // Must match MSI
    static readonly TimeSpan RestTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<Worker> _log = log;
    private readonly ILogger _childLog = loggers.CreateLogger("Sorcerer.Child");
    private Process? _child;
    private volatile bool _stopping;
    private volatile Uri? _restFromLog;
    private DateTimeOffset _portWarned = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var svcEnv = ServiceEnv.Read(ServiceName);

        var childExe = ServiceEnv.Get(svcEnv, "X14_CHILD_EXE");
        var childArgs = ServiceEnv.Get(svcEnv, "X14_CHILD_ARGS", "") ?? "";
        var childCwd = ServiceEnv.Get(svcEnv, "X14_CHILD_CWD", AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

        _log.LogInformation("Service starting. ChildExe={ChildExe} Args='{Args}' Cwd={Cwd}",
            childExe ?? "(none)", childArgs, childCwd);

        if (string.IsNullOrWhiteSpace(childExe))
            return;

        ServiceRecovery.Configure(ServiceName, _log);
        Directory.CreateDirectory(childCwd);

        var exePath = PathHelpers.ResolvePath(childExe!);
        var exeDir = PathHelpers.ResolvePath(".");                   // resolve current working folder (likely bin)
        var baseDir = Path.GetFullPath(Path.Combine(exeDir, ".."));  // step up one level
        var cfgPath = Path.Combine(baseDir, "config", "connect.cfg");

        _log.LogInformation("Child executable : {Path} Exists={Exists}", exePath, File.Exists(exePath));
        _log.LogInformation("Cfg: {Path} Exists={Exists}", cfgPath, File.Exists(cfgPath));

        if (!File.Exists(exePath))
        {
            _log.LogError("Will idle .. since child executable not found: {Path}", exePath);
            return; // keep running as a service, but don't crash
        }

        _log.LogInformation("X14 CDC is about to traverse sources from config: " + cfgPath);
        var (validPaths, missingPaths) = PathHelpers.ReadValidAndMissingPaths(cfgPath);
        foreach (var v in validPaths)
            _log.LogInformation("Path from connect.cfg exists: " + v);
        if (validPaths.Length == 0 && missingPaths.Length == 0)
            _log.LogInformation("connect.cfg exists but contains no valid or missing paths: " + cfgPath);
        foreach (var m in missingPaths)
            _log.LogWarning("Given path to X14 CDC source config: {Path} in connect.cfg path does not exist", m);
        if (validPaths.Length > 0)
        {
            childArgs = $"{childArgs} {PathHelpers.PathsToArgs(validPaths)}";
            _log.LogInformation("Appended {Count} paths from {Cfg}", validPaths.Length, cfgPath);
        }
        _log.LogInformation("Final child command: \"{Exe}\" {Args}", exePath, childArgs);

        var health = HealthSettings.From(svcEnv);
        _log.LogInformation("Health check: {Health}", health);
        var policy = new HealthPolicy(new HealthOptions());
        var backoff = new RestartBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        while (!Stopping(stoppingToken))
        {
            var started = DateTimeOffset.UtcNow;
            Process child;
            try
            {
                child = StartChild(exePath, childArgs, childCwd);
            }
            catch (Exception ex)
            {
                var wait = backoff.Next(TimeSpan.Zero);
                _log.LogError(new EventId(EventIds.ChildExited), ex,
                    "Failed to start the child process. Trying again in {Wait} s.", (int)wait.TotalSeconds);
                if (!await Delay(wait, stoppingToken))
                    break;
                continue;
            }
            policy.ChildStarted();

            var restartedByHealthCheck = await SuperviseAsync(child, started, policy, health, baseDir, cfgPath, stoppingToken);
            if (Stopping(stoppingToken))
                break;
            if (restartedByHealthCheck)
            {
                backoff.Reset();
                continue;
            }

            // The child exited by itself (a crash, or ConnectStandalone giving up on a config error).
            // Before, the service stayed Running without it; now it is started again.
            var ran = DateTimeOffset.UtcNow - started;
            var delay = backoff.Next(ran);
            _log.LogError(new EventId(EventIds.ChildExited),
                "The child process exited with code {Code} after {Ran}. Starting it again in {Wait} s.",
                ExitCode(child), Format(ran), (int)delay.TotalSeconds);
            if (!await Delay(delay, stoppingToken))
                break;
        }

        _log.LogInformation("ExecuteAsync exiting.");
    }

    private Process StartChild(string exePath, string childArgs, string childCwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = childArgs,
            WorkingDirectory = childCwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _restFromLog = null;
        var child = new Process { StartInfo = psi, EnableRaisingEvents = true };
        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _childLog.LogInformation("[child] {Line}", e.Data);
            if (_restFromLog is null && RestEndpoint.FromLogLine(e.Data) is { } uri)
            {
                _restFromLog = uri;
                _log.LogInformation(new EventId(EventIds.RestEndpointFound), "Kafka Connect REST API at {Uri}", uri);
            }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _childLog.LogError("[child] {Line}", e.Data);
        };

        if (!child.Start())
            throw new InvalidOperationException("Child process failed to start.");
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        _child = child;
        _log.LogInformation(new EventId(EventIds.ChildStarted), "Child PID {Pid} started. Command: \"{Exe}\" {Args}",
            child.Id, exePath, childArgs);
        return child;
    }

    /// <summary>
    /// Waits for the child to exit, running the health check every interval once the grace
    /// period after the start has passed. True when the health check restarted the child.
    /// </summary>
    private async Task<bool> SuperviseAsync(Process child, DateTimeOffset started, HealthPolicy policy,
        HealthSettings health, string baseDir, string cfgPath, CancellationToken ct)
    {
        var exited = child.WaitForExitAsync(CancellationToken.None);
        var nextCheck = started + health.Grace;
        while (!Stopping(ct))
        {
            var tick = health.Enabled
                ? Task.Delay(Max(nextCheck - DateTimeOffset.UtcNow, TimeSpan.Zero), ct)
                : Task.Delay(Timeout.Infinite, ct);
            var done = await Task.WhenAny(exited, tick);
            if (done == exited || Stopping(ct))
                return false;

            nextCheck = DateTimeOffset.UtcNow + health.Interval;
            var reason = await HealthCheckAsync(policy, baseDir, cfgPath, ct);
            if (reason is not null && !Stopping(ct))
            {
                KillChild(child);
                return true;
            }
        }
        return false;
    }

    /// <summary>One health check. Returns a reason when the child should be restarted.</summary>
    private async Task<string?> HealthCheckAsync(HealthPolicy policy, string baseDir, string cfgPath, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var cfg = ConnectConfig.Read(cfgPath, baseDir);
            var worker = JavaProperties.Load(cfg.Worker);
            var uri = _restFromLog ?? RestEndpoint.FromListeners(worker.GetValueOrDefault("listeners"));
            if (uri is null)
            {
                if (now - _portWarned >= TimeSpan.FromDays(1))
                {
                    _log.LogWarning(new EventId(EventIds.RestPortUnknown),
                        "The REST API address is unknown: no Jetty \"Started\" line from the child and no fixed port in listeners='{Listeners}' ({Worker}). Only the child process is supervised.",
                        worker.GetValueOrDefault("listeners"), cfg.Worker);
                    _portWarned = now;
                }
                return null;
            }

            using var cert = uri.Scheme == "https" ? ConnectConfig.WorkerCertificate(worker, baseDir) : null;
            using var client = new ConnectRestClient(uri, cert, RestTimeout);

            IReadOnlyList<ConnectorStatus> status;
            try
            {
                status = await client.GetStatusAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var why = client.PinMismatch ? "the server did not present the worker's own certificate" : Describe(ex);
                var d = policy.RestFailed(now, uri, why);
                Emit(d.Events);
                return d.RestartChildReason;
            }

            var databases = ConnectConfig.SourceDatabases(cfg.Sources);
            var checks = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            foreach (var c in status.Where(c => c.FailedParts().Count > 0))
                if (databases.TryGetValue(c.Name, out var db))
                    checks[c.Name] = ($"{db.Host}:{db.Port}", await Tcp.AnswersAsync(db.Host, db.Port, TcpTimeout, ct));

            var decision = policy.StatusReceived(now, status, name => checks.TryGetValue(name, out var v) ? v : null);
            Emit(decision.Events);
            foreach (var name in decision.RestartConnectors)
            {
                Exception? error = null;
                try
                {
                    await client.RestartFailedAsync(name, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    error = ex;
                }
                Emit([policy.RestartSent(name, error)]);
            }
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(new EventId(EventIds.HealthCheckError), ex, "The health check failed: {Message}", ex.Message);
            return null;
        }
    }

    private void Emit(IEnumerable<HealthEvent> events)
    {
        foreach (var e in events)
        {
            var level = e.Severity switch
            {
                Severity.Error => LogLevel.Error,
                Severity.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            };
            _log.Log(level, new EventId(e.Id), "{Message}", e.Message);
        }
    }

    private void KillChild(Process child)
    {
        try
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            child.WaitForExit(30_000);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Kill failed.");
        }
    }

    private bool Stopping(CancellationToken ct) => _stopping || ct.IsCancellationRequested;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static async Task<bool> Delay(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string ExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch { return "?"; }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;
        return inner == ex ? ex.Message : $"{ex.Message} {inner.Message}";
    }

    public override async Task StopAsync(CancellationToken token)
    {
        // Set first, so the supervision loop never takes the child's exit for a crash.
        _stopping = true;
        _log.LogInformation("Stop requested.");
        var child = _child;
        if (child is { HasExited: false })
        {
            try
            {
                // Attempt graceful exit (no window for console apps; this is best-effort)
                child.CloseMainWindow();
            }
            catch
            {
                /* ignore */
            }

            // Hard kill after grace period
            var waited = child.WaitForExit(10_000);
            if (!waited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Kill failed.");
                }
            }
        }

        await base.StopAsync(token);
        _log.LogInformation("Stopped.");
    }
}

/// <summary>
/// X14_HEALTH=off turns the health check off (the child is still restarted when it exits).
/// X14_HEALTH_INTERVAL_SECONDS (60) and X14_HEALTH_GRACE_SECONDS (300, after each start of the
/// child) can be set in the service's registry Environment.
/// </summary>
sealed record HealthSettings(bool Enabled, TimeSpan Interval, TimeSpan Grace)
{
    public static HealthSettings From(Dictionary<string, string> env)
    {
        var flag = (ServiceEnv.Get(env, "X14_HEALTH", "on") ?? "on").Trim().ToLowerInvariant();
        var enabled = flag is not ("off" or "false" or "0" or "no");
        return new HealthSettings(enabled, Seconds(env, "X14_HEALTH_INTERVAL_SECONDS", 60), Seconds(env, "X14_HEALTH_GRACE_SECONDS", 300));
    }

    static TimeSpan Seconds(Dictionary<string, string> env, string name, int fallback) =>
        TimeSpan.FromSeconds(int.TryParse(ServiceEnv.Get(env, name), out var s) && s > 0 ? s : fallback);

    public override string ToString() =>
        Enabled ? $"every {(int)Interval.TotalSeconds} s, starting {(int)Grace.TotalSeconds} s after each start of the child" : "off";
}

/// <summary>
/// Lets Windows restart Sorcerer if its own process dies: after 1 min, 1 min, then 5 min, with
/// the count reset after a day. failureflag 1 applies the actions also when the service stops
/// with an error exit code. A normal stop is not a failure.
/// </summary>
static class ServiceRecovery
{
    public static void Configure(string serviceName, ILogger log)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            Run("sc.exe", $"failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/300000");
            Run("sc.exe", $"failureflag \"{serviceName}\" 1");
            log.LogInformation("Recovery actions set: restart the service after 1 min, 1 min and 5 min.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not set the service's recovery actions.");
        }
    }

    static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{exe} did not start");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args} exited with {p.ExitCode}: {output.Trim()}");
    }
}


static class ServiceEnv
{
    public static Dictionary<string, string> Read(string serviceName)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", false);
        if (key is null) return dict;

        if (key.GetValue("Environment") is string[] multi)
        {
            foreach (var line in multi)
            {
                var idx = line.IndexOf('=');
                if (idx > 0)
                {
                    var name = line[..idx].Trim();
                    var val = line[(idx + 1)..];
                    if (name.Length > 0) dict[name] = val;
                }
            }
        }

        return dict;
    }

    public static string? Get(Dictionary<string, string> env, string name, string? fallback = null)
    {
        if (env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }
}


static class PathHelpers
{
    public static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.Combine(AppContext.BaseDirectory, path);
    }
    // Read lines from file, trim, keep only existing files
    public static (string[] valid, string[] missing) ReadValidAndMissingPaths(string pathFile)
    {
        if (!File.Exists(pathFile)) return (Array.Empty<string>(), Array.Empty<string>());

        try
        {
            // Determine base folder (one level above current bin folder)
            var exeDir = PathHelpers.ResolvePath(".");
            var parentDir = Path.GetFullPath(Path.Combine(exeDir, ".."));

            var all = File.ReadAllLines(pathFile)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(p =>
                {
                    // Make each path absolute relative to parentDir
                    return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(parentDir, p));
                })
                .ToArray();

            var valid = all.Where(File.Exists).ToArray();
            var missing = all.Except(valid).ToArray();

            return (valid, missing);
        }
        catch
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }
    }
    // Join paths into CLI-safe string (quoted)
    public static string PathsToArgs(string[] paths)
    {
        return string.Join(' ', paths.Select(p => $"\"{p}\""));
    }
}
