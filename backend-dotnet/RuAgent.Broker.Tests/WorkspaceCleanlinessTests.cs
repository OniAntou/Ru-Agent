using System.Diagnostics;
using RuAgent.Broker.Policy;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WorkspaceCleanlinessTests
{
    [Fact]
    public void Missing_directory_is_not_clean()
    {
        var provider = new GitWorkspaceCleanlinessProvider();
        Assert.False(provider.IsWorkspaceClean(
            Path.Combine(Path.GetTempPath(), "ru-no-such-dir-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Non_git_directory_is_not_clean()
    {
        using var temp = TempDir.Create();
        Assert.False(new GitWorkspaceCleanlinessProvider().IsWorkspaceClean(temp.Path));
    }

    [Fact]
    public void Clean_git_repo_is_clean_and_dirty_is_not()
    {
        using var temp = TempDir.Create();
        var provider = new GitWorkspaceCleanlinessProvider();
        Git(temp.Path, "init");
        Git(temp.Path, "-c user.email=t@t -c user.name=t commit --allow-empty -m seed");
        Assert.True(provider.IsWorkspaceClean(temp.Path));

        File.WriteAllText(Path.Combine(temp.Path, "note.txt"), "dirty");
        Git(temp.Path, "add note.txt");
        // A fresh probe sees the dirt; the first provider may still serve its
        // 60s cached clean verdict.
        Assert.False(new GitWorkspaceCleanlinessProvider().IsWorkspaceClean(temp.Path));
    }

    /// <summary>
    /// An untracked file is dirt. This is the case that matters most: a secret dropped
    /// into a workspace is normally never staged, and <c>git diff --quiet</c> plus
    /// <c>git diff --cached --quiet</c> both report a clean tree for it, which would
    /// unlock the standard clean-workspace auto-allow over a workspace carrying new credentials.
    /// </summary>
    [Fact]
    public void Untracked_file_makes_the_workspace_dirty()
    {
        using var temp = TempDir.Create();
        Git(temp.Path, "init");
        Git(temp.Path, "-c user.email=t@t -c user.name=t commit --allow-empty -m seed");
        Assert.True(new GitWorkspaceCleanlinessProvider().IsWorkspaceClean(temp.Path));

        File.WriteAllText(Path.Combine(temp.Path, ".env"), "SECRET=1");

        Assert.False(new GitWorkspaceCleanlinessProvider().IsWorkspaceClean(temp.Path));
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Assert.True(process.Start(), "git binary is required for cleanliness tests.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "git timed out.");
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string path) => Path = path;
        public string Path { get; }
        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-clean-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
