// Ties java.exe to Sorcerer's process, and cleans up java.exe left behind by an earlier run.
//
// On app10 (2026-11-02) Sorcerer crashed twice within 13 minutes. Windows restarted it each
// time and it started a new java.exe while the old ones kept running: three JVMs, all running
// the same connectors. KillChild and StopAsync only run when the service stops in order.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ConnectAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// A Job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE that every child is put in. Sorcerer
/// holds the only handle and never closes it, so Windows closes it when Sorcerer's process ends,
/// however it ends (a crash, taskkill /F, Stop-Process), and that kills the child.
/// </summary>
static class ChildJob
{
    // A raw handle on purpose: a SafeHandle could be finalized and closed, which kills the child.
    static IntPtr _job;

    public static void Create(ILogger log)
    {
        if (!OperatingSystem.IsWindows() || _job != IntPtr.Zero)
            return;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            log.LogError(new Win32Exception(), "Could not create a Job object. java.exe will not die with the service.");
            return;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            log.LogError(new Win32Exception(), "Could not set KILL_ON_JOB_CLOSE on the Job object. java.exe will not die with the service.");
            CloseHandle(job);
            return;
        }
        _job = job;
    }

    /// <summary>Puts a just started child in the job, so it dies with Sorcerer.</summary>
    public static void Add(Process child, ILogger log)
    {
        if (!OperatingSystem.IsWindows() || _job == IntPtr.Zero)
            return;
        if (!AssignProcessToJobObject(_job, child.SafeHandle))
            log.LogError(new Win32Exception(), "Could not put child PID {Pid} in the Job object. It will not die with the service.", child.Id);
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// At service start, before the first child: kills every java.exe (and every process with the
/// child's file name) whose executable lies in the install directory. None of them can be ours,
/// so they are left over from an earlier run that died without killing its child: an older
/// Sorcerer without the Job object, or the moment between starting a child and putting it in
/// the job. Each one is logged as Warning 2004.
/// </summary>
static class Orphans
{
    public static void Kill(string installDir, string childExe, ILogger log)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (WindowsPath.IsRoot(installDir))
        {
            log.LogWarning("Not looking for a left-over java.exe: the install directory {Dir} is a root.", installDir);
            return;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "java", "javaw", Path.GetFileNameWithoutExtension(childExe) };
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    var image = ImagePath(p);
                    if (image is null || !WindowsPath.IsUnder(installDir, image))
                        continue;
                    var started = StartTime(p);
                    try
                    {
                        p.Kill();
                        p.WaitForExit(10_000);
                        log.LogWarning(new EventId(EventIds.OrphanKilled),
                            "Killed PID {Pid} ({Image}, started {Started}): left over from an earlier run of the service, which did not stop it.",
                            p.Id, image, started);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(new EventId(EventIds.OrphanKilled), ex,
                            "Could not kill PID {Pid} ({Image}, started {Started}), left over from an earlier run of the service.",
                            p.Id, image, started);
                    }
                }
            }
        }
    }

    static string StartTime(Process p)
    {
        try { return p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return "?"; }
    }

    /// <summary>The full path of a process's executable, or null when it cannot be read.</summary>
    static string? ImagePath(Process p)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
        if (handle.IsInvalid)
            return null;
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
}
