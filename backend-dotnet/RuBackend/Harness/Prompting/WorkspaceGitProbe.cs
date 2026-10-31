using System.Diagnostics;
using System.Text;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Reports a bounded git state for the prompt environment. The model needs to know
/// that the workspace holds work in progress before it edits anything; the probe
/// returns counts only, never file names, so the prompt stays free of paths. Counts
/// come from the Broker's porcelain-v2 parser instead of a second ad-hoc parser.
/// </summary>
internal static class WorkspaceGitProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string Describe(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return "unknown";
        }

        var top = Run(root, ["rev-parse", "--show-toplevel"]);
        if (top.ExitCode != 0)
        {
            return "not_a_repository";
        }

        var status = Run(root, ["status", "--porcelain=v2", "--branch"]);
        if (status.ExitCode != 0)
        {
            return "unknown";
        }

        var parsed = GitStatusParser.Parse(status.Stdout);
        var untracked = parsed.Entries.Count(entry => entry.Kind == RunGitEntryKind.Untracked);
        var changed = parsed.Entries.Count - untracked;
        if (changed == 0 && untracked == 0)
        {
            return parsed.Truncated ? "unknown" : "clean";
        }

        var suffix = parsed.Truncated ? "+" : string.Empty;
        return $"dirty ({changed}{suffix} changed, {untracked}{suffix} untracked)";
    }

    private static (int ExitCode, string Stdout) Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Localized messages and optional locks would only add noise: this probe
        // reads and nothing else, and it must not race the user's own git commands.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(Timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the timeout and the kill.
                }

                return (-1, string.Empty);
            }

            return (process.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
}
