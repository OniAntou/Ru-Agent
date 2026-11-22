using System.ComponentModel;
using System.Diagnostics;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// Attaches a launched browser to a kill-on-close Job Object so the browser tree dies with the
/// Host even if the Host is killed or crashes (ADR 0055). Attachment is best effort: a browser
/// that cannot join the job still runs and is still killed by the engine's own disposal; the
/// job only closes the crash/forced-exit leak.
/// </summary>
internal static class BrowserProcessJob
{
    internal static WindowsJobProcessTree? TryAttach(Process process)
    {
        WindowsJobProcessTree? job = null;
        try
        {
            job = WindowsJobProcessTree.Create();
            job.Assign(process.Id);
            return job;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            job?.Dispose();
            return null;
        }
    }

    internal static void Release(WindowsJobProcessTree? job)
    {
        if (job is null) return;
        try
        {
            job.KillTree();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The browser is already gone; disposal below still closes the handle.
        }

        job.Dispose();
    }
}
