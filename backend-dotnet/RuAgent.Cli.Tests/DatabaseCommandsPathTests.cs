using RuAgent.Hosting;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class DatabaseCommandsPathTests
{
    [Fact]
    public void DebugCliPrefersDebugBackendAheadOfStaleReleasePublish()
    {
        using var layout = new TemporaryLayout();
        var staleRelease = layout.Add("backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "win-x64", "publish", RuAppLayout.HostExecutableName);
        var debugBackend = layout.Add("backend-dotnet", "RuBackend", "bin", "Debug", "net10.0", RuAppLayout.HostExecutableName);
        var appBase = layout.Directory("backend-dotnet", "RuAgent.Cli", "bin", "Debug", "net10.0");

        Assert.Equal(debugBackend, DatabaseCommands.ResolveBackendExecutable(layout.Root, appBase, "Debug"));
        Assert.NotEqual(staleRelease, debugBackend);
    }

    [Fact]
    public void ReleaseCliPrefersReleaseBackendAndFlatInstallKeepsSiblingHostPriority()
    {
        using var layout = new TemporaryLayout();
        layout.Add("backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "win-x64", "publish", RuAppLayout.HostExecutableName);
        var releaseBackend = layout.Add("backend-dotnet", "RuBackend", "bin", "Release", "net10.0", RuAppLayout.HostExecutableName);
        var appBase = layout.Directory("backend-dotnet", "RuAgent.Cli", "bin", "Release", "net10.0");
        Assert.Equal(releaseBackend, DatabaseCommands.ResolveBackendExecutable(layout.Root, appBase, "Release"));

        var flatHost = layout.Add("flat-install", RuAppLayout.HostExecutableName);
        Assert.Equal(flatHost, DatabaseCommands.ResolveBackendExecutable(layout.Root, Path.GetDirectoryName(flatHost)!, "Debug"));
    }

    private sealed class TemporaryLayout : IDisposable
    {
        public TemporaryLayout() => Root = Path.Combine(Path.GetTempPath(), "ru-db-path-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }

        public string Directory(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string Add(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
