// .NET 8 Windows Service that can supervise a child process (optional).
// Defaults: runs idle loop. Set env X14_CHILD_EXE / X14_CHILD_ARGS to run a child.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "Connect Service")
    .ConfigureLogging((ctx, logging) =>
    {
        logging.ClearProviders();
        logging.AddEventLog(cfg => { cfg.SourceName = "ConnectSvc"; cfg.LogName = "Application"; });
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
        _log.LogInformation("Service starting. ChildExe={ChildExe} Args='{Args}' Cwd={Cwd}",
            ChildExe ?? "(none)", ChildArgs, ChildCwd);

        if (!string.IsNullOrWhiteSpace(ChildExe))
        {
            Directory.CreateDirectory(ChildCwd);
            var exePath = Path.IsPathRooted(ChildExe!) ? ChildExe! : Path.Combine(AppContext.BaseDirectory, ChildExe!);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = ChildArgs,
                WorkingDirectory = ChildCwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true
            };

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
            _log.LogInformation("Child PID {Pid} started.", _child.Id);
        }

        // Idle loop or watchdog while running
        try
        {
            while (!stoppingToken.IsCancellationRequested &&
                   (_child is null || !_child.HasExited))
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* normal on stop */ }

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
