// .NET 8 Windows Service that can supervise a child process (optional).
// Defaults: runs idle loop. Set env X14_CHILD_EXE / X14_CHILD_ARGS to run a child.
using System.Collections.Generic;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "Sorcerer")
    .ConfigureLogging((ctx, logging) =>
    {
        logging.ClearProviders();
        logging.AddEventLog(cfg => { cfg.SourceName = "Sorcerer"; cfg.LogName = "Application"; });
        logging.AddConsole();
    })
    .ConfigureServices(services => services.AddHostedService<Worker>());

await builder.Build().RunAsync();

sealed class Worker(ILogger<Worker> log) : BackgroundService
{
    private readonly ILogger<Worker> _log = log;
    private Process? _child;

    // Configure child via env vars (optional). If X14_CHILD_EXE is unset, runs an idle loop.
    private static readonly string? ChildExe  = GetEnvPath("X14_CHILD_EXE"); // e.g. "jdk\\bin\\java.exe" or "agent\\agent.exe"
    private static readonly string  ChildArgs = Environment.GetEnvironmentVariable("X14_CHILD_ARGS") ?? "";
    private static readonly string  ChildCwd  = Environment.GetEnvironmentVariable("X14_CHILD_CWD")
                                                ?? AppContext.BaseDirectory;

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var serviceName = "Sorcerer"; // must match MSI
    var svcEnv   = ServiceEnv.Read(serviceName);

    var childExe  = ServiceEnv.Get(svcEnv, "X14_CHILD_EXE");
    var childArgs = ServiceEnv.Get(svcEnv, "X14_CHILD_ARGS", "");
    var childCwd  = ServiceEnv.Get(svcEnv, "X14_CHILD_CWD", AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    _log.LogInformation("Service starting. ChildExe={ChildExe} Args='{Args}' Cwd={Cwd}",
        childExe ?? "(none)", childArgs, childCwd);


    if (!string.IsNullOrWhiteSpace(childExe))
    {
        Directory.CreateDirectory(childCwd);

        var exePath = Path.IsPathRooted(childExe!)
            ? childExe!
            : Path.Combine(AppContext.BaseDirectory, childExe!);

        if (!File.Exists(exePath))
        {
            _log.LogError("Child executable not found: {Path}", exePath);
            // keep running as a service, but don’t crash
        }
        else
        {


            // check connect.cfg
            var cfgPath = Path.Combine(childCwd, "config", "connect.cfg");
            _log.LogInformation("X14 CDC will read sources from config: " + cfgPath);

            if (File.Exists(cfgPath))
            {
                _log.LogInformation("X14 CDC is about to traverse sources from config: " + cfgPath);

                var (validPaths, missingPaths) = PathHelpers.ReadValidAndMissingPaths(cfgPath);


                // log valid paths
                foreach (var v in validPaths)
                    _log.LogInformation("Path from connect.cfg exists: " + v);

                // log if both empty
                if (validPaths.Length == 0 && missingPaths.Length == 0)
                    _log.LogInformation("connect.cfg exists but contains no valid or missing paths: " + cfgPath);

                foreach (var m in missingPaths)
                    _log.LogWarning("Given path to X14 CDC source config: {Path} in connect.cfg path does not exist", m);

                if (validPaths.Length > 0)
                {
                    childArgs = $"{childArgs} {PathHelpers.PathsToArgs(validPaths)}";
                    _log.LogInformation("Appended {Count} paths from {Cfg}", validPaths.Length, cfgPath);
                }
            }


            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = childArgs,
                WorkingDirectory = childCwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true
            };

            // optional: propagate the same env to the child (not required for your current X14_CHILD_* use)
            // foreach (var kv in svcEnv) psi.Environment[kv.Key] = kv.Value;

            _child = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _child.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.LogInformation("[child] {Line}", e.Data); };
            _child.ErrorDataReceived  += (_, e) => { if (e.Data is not null) _log.LogError("[child] {Line}", e.Data); };

            if (!_child.Start())
            {
                _log.LogError("Failed to start child process.");
                throw new InvalidOperationException("Child process failed to start.");
            }
            _child.BeginOutputReadLine();
            _child.BeginErrorReadLine();
            _log.LogInformation("Child PID {Pid} started. Command: \"{Exe}\" {Args}", _child.Id, exePath, childArgs);

        }
    }

    try
    {
        while (!stoppingToken.IsCancellationRequested && (_child is null || !_child.HasExited))
            await Task.Delay(1000, stoppingToken);
    }
    catch (OperationCanceledException) { }

    _log.LogInformation("ExecuteAsync exiting.");
}
    public override async Task StopAsync(CancellationToken token)
    {
        _log.LogInformation("Stop requested.");
        if (_child is { HasExited: false })
        {
            try
            {
                // Attempt graceful exit (no window for console apps; this is best-effort)
                _child.CloseMainWindow();
            }
            catch { /* ignore */ }

            // Hard kill after grace period
            var waited = _child.WaitForExit(10_000);
            if (!waited)
            {
                try { _child.Kill(entireProcessTree: true); }
                catch (Exception ex) { _log.LogWarning(ex, "Kill failed."); }
            }
        }
        await base.StopAsync(token);
        _log.LogInformation("Stopped.");
    }

    private static string? GetEnvPath(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return null;
        return v.Replace('/', Path.DirectorySeparatorChar);
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
                    var val  = line[(idx + 1)..];
                    if (name.Length > 0) dict[name] = val;
                }
            }
        }
        return dict;
    }

    public static string? Get(Dictionary<string,string> env, string name, string? fallback = null)
    {
        if (env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }
}


static class PathHelpers
{

    // Read lines from file, trim, keep only existing files
public static (string[] valid, string[] missing) ReadValidAndMissingPaths(string pathFile)
{
    if (!File.Exists(pathFile)) return (Array.Empty<string>(), Array.Empty<string>());

    try
    {
        var all = File.ReadAllLines(pathFile)
                      .Select(l => l.Trim())
                      .Where(l => !string.IsNullOrEmpty(l))
                      .ToArray();

        var valid   = all.Where(File.Exists).ToArray();
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
