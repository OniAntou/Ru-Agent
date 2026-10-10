using RuAgent.Hosting;
using Xunit;

namespace RuAgent.Hosting.Tests;

/// <summary>
/// The layout resolver decides where a running Ru finds the sibling host
/// executable. A distribution gets one flat directory; a repository checkout
/// keeps the host under its own project. Both have to resolve, and the flat
/// layout has to win, because a packaged install must never depend on repository
/// structure.
/// </summary>
[Collection(nameof(LayoutResolutionCollection))]
public sealed class RuAppLayoutTests
{
    [Fact]
    public void Flat_layout_is_recognized_when_the_host_sibling_exists()
    {
        using var flat = TempLayout.Create();

        Assert.True(RuAppLayout.IsFlatLayout(flat.Path));

        File.Delete(Path.Combine(flat.Path, RuAppLayout.HostExecutableName));
        Assert.False(RuAppLayout.IsFlatLayout(flat.Path));
    }

    [Fact]
    public void Missing_or_empty_directory_is_not_a_flat_layout()
    {
        Assert.False(RuAppLayout.IsFlatLayout(string.Empty));
        Assert.False(RuAppLayout.IsFlatLayout(Path.Combine(Path.GetTempPath(), "ru-does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Sibling_host_resolves_with_no_repository_anywhere()
    {
        using var flat = TempLayout.Create();

        // No project root at all: this is the installed case, and it is exactly the
        // case that used to throw FileNotFoundException regardless of the layout.
        Assert.Equal(
            Path.Combine(flat.Path, RuAppLayout.HostExecutableName),
            RuAppLayout.ResolveHostExecutable(projectRoot: null, flat.Path));
    }

    [Fact]
    public void Repository_layout_still_resolves_when_there_is_no_sibling()
    {
        using var repo = TempLayout.CreateRepository();

        Assert.False(RuAppLayout.IsFlatLayout(repo.AppDirectory));

        Assert.Equal(repo.HostInRepo, RuAppLayout.ResolveHostExecutable(repo.Path, repo.AppDirectory));
    }

    [Fact]
    public void Sibling_wins_over_a_repository_that_also_has_the_host()
    {
        using var repo = TempLayout.CreateRepository();
        using var flat = TempLayout.Create();

        // Both layouts are complete. The distribution must win, because the sibling
        // is the copy that shipped with this build.
        Assert.Equal(
            Path.Combine(flat.Path, RuAppLayout.HostExecutableName),
            RuAppLayout.ResolveHostExecutable(repo.Path, flat.Path));
    }

    [Fact]
    public void Unresolvable_layout_returns_null_rather_than_a_bad_guess()
    {
        var empty = Path.Combine(Path.GetTempPath(), "ru-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Null(RuAppLayout.ResolveHostExecutable(projectRoot: null, empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Working_root_falls_back_to_the_application_directory()
    {
        using var flat = TempLayout.Create();

        // The installed case: no repository to adopt, so the directory holding the
        // application becomes the root instead of a null that stops startup.
        Assert.Equal(flat.Path, RuAppLayout.ResolveWorkingRoot(projectRoot: null, flat.Path));
        Assert.Equal(flat.Path, RuAppLayout.ResolveWorkingRoot(
            projectRoot: Path.Combine(flat.Path, "does-not-exist"),
            flat.Path));
    }

    [Fact]
    public void Working_root_prefers_an_existing_project_root()
    {
        using var repo = TempLayout.CreateRepository();
        using var flat = TempLayout.Create();

        Assert.Equal(repo.Path, RuAppLayout.ResolveWorkingRoot(repo.Path, flat.Path));
    }

    [Fact]
    public void Environment_override_beats_both_layouts()
    {
        using var flat = TempLayout.Create();
        var overrideFile = Path.Combine(flat.Path, "custom-host.exe");
        File.WriteAllText(overrideFile, "test");

        var previous = Environment.GetEnvironmentVariable("RU_AGENT_HOST_EXE");
        try
        {
            Environment.SetEnvironmentVariable("RU_AGENT_HOST_EXE", overrideFile);
            Assert.Equal(overrideFile, RuAppLayout.ResolveHostExecutable(projectRoot: null, flat.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_AGENT_HOST_EXE", previous);
        }
    }

    internal sealed class TempLayout : IDisposable
    {
        private TempLayout(string path, string appDirectory, string hostInRepo)
        {
            Path = path;
            AppDirectory = appDirectory;
            HostInRepo = hostInRepo;
        }

        public string Path { get; }
        public string AppDirectory { get; }
        public string HostInRepo { get; }

        /// <summary>A distribution: the host sits beside the running app.</summary>
        public static TempLayout Create()
        {
            var root = NewRoot("ru-flat-");
            File.WriteAllText(System.IO.Path.Combine(root, RuAppLayout.HostExecutableName), "test");

            return new TempLayout(root, root, string.Empty);
        }

        /// <summary>A build tree: the host under its own project, plus markers.</summary>
        public static TempLayout CreateRepository()
        {
            var root = NewRoot("ru-repo-");
            File.WriteAllText(System.IO.Path.Combine(root, "pyproject.toml"), "[project]");
            Directory.CreateDirectory(System.IO.Path.Combine(root, "backend-dotnet"));

            var appDirectory = System.IO.Path.Combine(root, "gui-winui3", "bin", "Release", "net10.0-windows", "win-x64", "publish");
            Directory.CreateDirectory(appDirectory);

            var host = WriteAt(root, "RuBackend", RuAppLayout.HostExecutableName);

            return new TempLayout(root, appDirectory, host);
        }

        private static string NewRoot(string prefix)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string WriteAt(string root, string project, string executable)
        {
            var directory = System.IO.Path.Combine(
                root, "backend-dotnet", project, "bin", "Release", "net10.0", "win-x64", "publish");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, executable);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Serialised because these tests set process-wide environment variables and the
/// resolver reads them. Mirrors the existing heavy-collection pattern.
/// </summary>
[CollectionDefinition(nameof(LayoutResolutionCollection), DisableParallelization = true)]
public sealed class LayoutResolutionCollection;
