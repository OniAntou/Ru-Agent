using System.Diagnostics;
using RuAgent.Hosting;

namespace RuAgent.Cli;

internal static class DatabaseCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("database_command_required");
            PrintUsage();
            return 2;
        }

        var projectRoot = ResolveProjectRoot();
        var backendPath = ResolveBackendExecutable(projectRoot, AppContext.BaseDirectory, GetSourceConfiguration(AppContext.BaseDirectory));
        if (backendPath is null)
        {
            Console.Error.WriteLine("ru_database_host_unavailable");
            return 1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = backendPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--database");
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("ru_database_host_start_failed");
            return 1;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Console.Error.WriteLine("cancelled");
            return 130;
        }
    }

    private static string ResolveProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);
        var current = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(current, "backend-dotnet"))) return Path.GetFullPath(current);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend-dotnet"))) return directory.FullName;
        return current;
    }

    internal static string? ResolveBackendExecutable(string projectRoot, string appBaseDirectory, string? sourceConfiguration)
    {
        var overridden = Environment.GetEnvironmentVariable("RU_AGENT_HOST_EXE");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
            return Path.GetFullPath(overridden);

        var sibling = Path.Combine(appBaseDirectory, RuAppLayout.HostExecutableName);
        if (File.Exists(sibling)) return Path.GetFullPath(sibling);

        if (sourceConfiguration is "Debug" or "Release")
        {
            var matchingBuild = FindBuildOutput(projectRoot, sourceConfiguration);
            if (matchingBuild is not null) return matchingBuild;
        }

        // Keep RuAppLayout's repository publish and flat-install fallback intact.
        return RuAppLayout.ResolveHostExecutable(projectRoot, appBaseDirectory)
            ?? FindBuildOutput(projectRoot, sourceConfiguration == "Debug" ? "Release" : "Debug");
    }

    private static string? FindBuildOutput(string projectRoot, string? configuration)
    {
        if (configuration is not ("Debug" or "Release")) return null;
        var backendRoot = Path.Combine(projectRoot, "backend-dotnet", "RuBackend", "bin", configuration, "net10.0");
        foreach (var candidate in new[]
        {
            Path.Combine(backendRoot, RuAppLayout.HostExecutableName),
            Path.Combine(backendRoot, "win-x64", "publish", RuAppLayout.HostExecutableName),
        })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    private static string? GetSourceConfiguration(string appBaseDirectory)
    {
        for (var directory = new DirectoryInfo(appBaseDirectory); directory is not null; directory = directory.Parent)
            if (directory.Name is "Debug" or "Release") return directory.Name;
        return null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ru database status [--json] [--data-root PATH]");
        Console.Error.WriteLine("       ru database backup --out PATH [--data-root PATH]");
        Console.Error.WriteLine("       ru database verify PATH [--data-root PATH]");
        Console.Error.WriteLine("       ru database restore PATH [--data-root PATH]");
        Console.Error.WriteLine("       ru database compact [--data-root PATH]");
        Console.Error.WriteLine("       ru database prune --older-than-days N [--apply] [--data-root PATH]");
    }
}
