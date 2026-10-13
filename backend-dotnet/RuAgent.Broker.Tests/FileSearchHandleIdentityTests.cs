using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileSearchHandleIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-search-identity-" + Guid.NewGuid().ToString("N"));

    public FileSearchHandleIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Search_allows_an_ordinary_candidate_with_the_authorized_final_path()
    {
        var file = Path.Combine(_root, "ordinary.txt");
        File.WriteAllText(file, "needle");

        var result = await new FileSearchAdapter(_root).ExecuteAsync(
            new JsonObject { ["query"] = "needle" }, CancellationToken.None);

        var match = Assert.Single(result["results"]!.AsArray());
        Assert.Equal("ordinary.txt", match!["path"]!.GetValue<string>());
        Assert.Equal("needle", match["preview"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_does_not_read_an_outside_file_after_a_candidate_directory_becomes_a_junction()
    {
        if (!OperatingSystem.IsWindows()) return;

        var outside = Path.Combine(Path.GetTempPath(), "ru-search-identity-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var result = await SearchAfterCandidateDirectorySwapAsync(outside, "secret.txt");

            Assert.Empty(result.Matches);
            Assert.True(result.Skipped);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    private async Task<SearchOutcome> SearchAfterCandidateDirectorySwapAsync(string targetDirectory, string fileName)
    {
        var candidateDirectory = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(candidateDirectory);
        File.WriteAllText(Path.Combine(candidateDirectory, fileName), "needle outside workspace");

        // Capture the path after the same early canonicalization used by ExecuteAsync.
        var candidatePath = new CanonicalPathResolver(_root).Resolve(Path.Combine("candidate", fileName)).CanonicalPath;

        Directory.Move(candidateDirectory, Path.Combine(_root, "candidate-authorized"));
        File.WriteAllText(Path.Combine(targetDirectory, fileName), "needle outside workspace");
        Assert.True(TryCreateJunction(candidateDirectory, targetDirectory), "Could not create the junction required for the deterministic swap.");

        // Exercise the candidate-opening stage directly so the junction swap is
        // deterministic and requires no production-only synchronization hook.
        var adapter = new FileSearchAdapter(_root);
        var method = typeof(FileSearchAdapter).GetMethod("SearchCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(adapter,
        [
            candidatePath,
            Path.Combine("candidate", fileName),
            "needle",
            null,
            false,
            10,
            Stopwatch.StartNew(),
            CancellationToken.None,
        ])!;
        await task;

        var outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var matches = (IReadOnlyList<JsonObject>)outcome.GetType().GetProperty("Matches")!.GetValue(outcome)!;
        var skipped = (bool)outcome.GetType().GetProperty("Skipped")!.GetValue(outcome)!;
        return new SearchOutcome(matches, skipped);
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record SearchOutcome(IReadOnlyList<JsonObject> Matches, bool Skipped);
}
