using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

namespace RuBackend.Harness.Hooks;

public sealed record HookResult(int ExitCode, string Message, bool TimedOut)
{
    /// <summary>Exit code that makes a PreToolUse hook reject the call.</summary>
    public const int BlockExitCode = 2;

    public bool Blocks => !TimedOut && ExitCode == BlockExitCode;
}

public interface IHookRunner
{
    Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a hook through <c>cmd.exe</c> with the same sanitized environment agent terminal
/// children get (credentials stripped), the event JSON on stdin, a hard timeout and a
/// bounded stderr capture. The hook starts suspended, joins its own Job Object before it
/// can run, and the job is killed when the call ends, so a descendant the hook spawned
/// cannot outlive it or hold its pipes open.
/// </summary>
public sealed class HookProcessRunner : IHookRunner
{
    private const int MaxMessageChars = 2048;

    public async Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } shell ? shell : "cmd.exe",
            Arguments = "/d /s /c \"" + hook.Command + "\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        foreach (var (name, value) in new AgentCommandEnvironment().ChildEnvironment) start.Environment[name] = value;
        start.Environment["RU_HOOK_EVENT"] = hook.Event.ToString();

        using var job = WindowsJobProcessTree.Create();
        SuspendedProcess.StartedProcess? started = null;
        try
        {
            // CREATE_SUSPENDED: the hook runs no user code until it is in the job,
            // so a descendant it spawns immediately cannot escape the tree cleanup.
            started = SuspendedProcess.StartSuspended(start, redirectStandardInput: true);
            job.Assign(started.Process.Id);
            if (!SuspendedProcess.Resume(started.ThreadHandle))
            {
                throw new InvalidOperationException("hook_start_failed");
            }

            started.ThreadHandle.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            DisposeStarted(started);
            throw new InvalidOperationException("hook_start_failed", error);
        }

        using var process = started.Process;
        using var stdin = started.StandardInput;
        using var stderrReader = started.StandardError;
        using var stdoutReader = started.StandardOutput;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(hook.Timeout);
        var stderr = ReadBoundedAsync(stderrReader, timeout.Token);
        var stdout = ReadBoundedAsync(stdoutReader, timeout.Token);
        try
        {
            if (stdin is not null)
            {
                try
                {
                    await stdin.WriteAsync(Encoding.UTF8.GetBytes(stdinJson), timeout.Token).ConfigureAwait(false);
                    await stdin.FlushAsync(timeout.Token).ConfigureAwait(false);
                    stdin.Close();
                }
                catch (IOException)
                {
                    // The hook may exit without reading its input.
                }
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            // cmd.exe exiting does not close the pipes while a descendant still holds
            // them. The drain keeps the same deadline instead of hanging the hook, and
            // a timeout that fires here is a real overrun, not a silent success.
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            if (timeout.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.KillTree();
                return new HookResult(-1, "hook timed out", true);
            }

            return new HookResult(process.ExitCode, Clean(stderr.Result), false);
        }
        catch (OperationCanceledException)
        {
            job.KillTree();
            TryKill(process);
            // The readers see the same cancellation and return their partial text;
            // awaiting them keeps the cancellation source alive until they finish.
            await DrainPipesAsync(stderr, stdout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new HookResult(-1, "hook timed out", true);
        }
        finally
        {
            // Kill-on-close: descendants that outlived cmd.exe (or a timed-out hook)
            // die with the job instead of being left behind as orphans.
            job.KillTree();
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (builder.Length < MaxMessageChars) builder.Append(buffer, 0, Math.Min(read, MaxMessageChars - builder.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // Partial output is still useful as a message.
        }
        catch (IOException)
        {
            // The child side of the pipe closed; the captured prefix is still useful.
        }
        catch (ObjectDisposedException)
        {
        }

        return builder.ToString();
    }

    private static async Task DrainPipesAsync(Task<string> stderr, Task<string> stdout)
    {
        try
        {
            await Task.WhenAll(stderr, stdout).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException)
        {
            // The hook is already reported as timed out; a stuck pipe reader must not
            // keep the call open past its bound.
        }
    }

    private static string Clean(string text) => text.Trim();

    private static void DisposeStarted(SuspendedProcess.StartedProcess? started)
    {
        if (started is null)
        {
            return;
        }

        try
        {
            if (!started.Process.HasExited)
            {
                started.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
        }

        started.ThreadHandle.Dispose();
        started.StandardInput?.Dispose();
        started.StandardOutput.Dispose();
        started.StandardError.Dispose();
        started.Process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
