using System.Diagnostics;
using System.Text;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Reports a bounded git state for the prompt environment. The model needs to know
/// that the workspace holds work in progress before it edits anything; the probe
/// returns counts only, never file names, so the prompt stays free of paths.
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

        var status = Run(root, ["status", "--porcelain"]);
        if (status.ExitCode != 0)
        {
            return "unknown";
        }

        var changed = 0;
        var untracked = 0;
        foreach (var raw in status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            if (line.StartsWith("??", StringComparison.Ordinal))
            {
                untracked++;
            }
            else
            {
                changed++;
            }
        }

        if (changed == 0 && untracked == 0)
        {
            return "clean";
        }

        return $"dirty ({changed} changed, {untracked} untracked)";
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
