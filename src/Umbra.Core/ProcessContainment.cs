using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Umbra;

/// <summary>
/// Keeps a started command and everything it spawns together, so a timeout, a cancel or the end of the run takes the whole tree down.
/// Windows uses a job object, Linux a process group.
/// </summary>
interface IProcessContainment : IDisposable
{
    /// <summary>Called after the process started, before anything is read from it. Throws <see cref="WidgetCommandException"/> when it can't be contained (the process is killed first).</summary>
    void Attach(Process proc);

    /// <summary>Ends the whole tree: used on timeout and cancel.</summary>
    Task KillAsync(Process proc);
}

/// <summary>Windows: a kill-on-close job object (see <see cref="ProcessJob"/>). Disposing terminates anything still in it.</summary>
sealed class JobContainment : IProcessContainment
{
    readonly IntPtr _job;

    JobContainment(IntPtr job) => _job = job;

    public static JobContainment Create()
    {
        var job = ProcessJob.CreateKillOnCloseJob();
        return job == IntPtr.Zero ? throw new WidgetCommandException("Could not create a job object to contain the program") : new JobContainment(job);
    }

    public void Attach(Process proc)
    {
        if (ProcessJob.AddToJob(_job, proc.Handle)) return;
        try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        throw new WidgetCommandException("Could not contain the program in a job object");
    }

    public Task KillAsync(Process proc)
    {
        ProcessJob.TerminateJobObject(_job, 1);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        ProcessJob.TerminateJobObject(_job, 1);
        ProcessJob.CloseHandle(_job);
    }
}

/// <summary>
/// Linux: the child was started through <c>setsid</c> (see <see cref="CommandLaunch"/>), so its process id is also its process group
/// id and <c>kill(-pgid, sig)</c> reaches every descendant that stayed in the group. On timeout or cancel the group gets SIGTERM, a
/// short grace period, then SIGKILL; the end of every run sends SIGKILL to whatever is left.
/// <para><b>Not run on Linux yet.</b> The only code here is the signal calls and the order they are made in; the argv is tested in
/// <c>CommandLaunchTests</c> and the end-to-end timeout case is the Linux-only test in <c>PosixCommandTests</c>.</para>
/// <para>Limits: a descendant that starts its own session (<c>setsid</c>, a daemon) leaves the group and survives; unlike a Windows
/// job, nothing kills the group if Umbra itself is killed.</para>
/// </summary>
[SupportedOSPlatform("linux")]
sealed partial class ProcessGroupContainment(TimeSpan? grace = null) : IProcessContainment
{
    const int SIGTERM = 15, SIGKILL = 9;
    readonly TimeSpan _grace = grace ?? TimeSpan.FromSeconds(2);
    int _pgid;

    [LibraryImport("libc.so.6", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    public void Attach(Process proc) => _pgid = proc.Id;

    public async Task KillAsync(Process proc)
    {
        if (_pgid <= 1) return; // never signal "-1" (everything) or "-0"
        Kill(-_pgid, SIGTERM);
        try { await proc.WaitForExitAsync().WaitAsync(_grace); } catch (TimeoutException) { }
        Kill(-_pgid, SIGKILL);
    }

    public void Dispose()
    {
        if (_pgid > 1) Kill(-_pgid, SIGKILL); // ESRCH when the group is already empty
    }
}
