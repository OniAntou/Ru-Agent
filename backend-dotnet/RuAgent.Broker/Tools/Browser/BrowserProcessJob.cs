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

    /// <summary>
    /// Waits, bounded, until the browser and every process it started have exited on their own.
    /// Firefox runs through a launcher that exits first, so the job's process count decides; a
    /// browser outside a job is judged by its own process.
    /// </summary>
    internal static async Task WaitForExitAsync(WindowsJobProcessTree? job, Process process, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            bool exited;
            try
            {
                exited = process.HasExited && (job?.ActiveProcessCount() ?? 0) == 0;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or ObjectDisposedException)
            {
                return;
            }

            if (exited) return;
            await Task.Delay(100).ConfigureAwait(false);
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
