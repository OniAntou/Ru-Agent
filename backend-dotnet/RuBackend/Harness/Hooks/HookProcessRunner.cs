using System.Diagnostics;
using System.Text;
using RuAgent.Broker.Tools.Terminal;

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
/// bounded stderr capture. The whole process tree is killed on timeout or cancellation.
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

        using var process = Process.Start(start) ?? throw new InvalidOperationException("hook_start_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(hook.Timeout);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinJson.AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The hook may exit without reading its input.
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            return new HookResult(process.ExitCode, Clean(await stderr.ConfigureAwait(false)), false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new HookResult(-1, "hook timed out", true);
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

        return builder.ToString();
    }

    private static string Clean(string text) => text.Trim();

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
