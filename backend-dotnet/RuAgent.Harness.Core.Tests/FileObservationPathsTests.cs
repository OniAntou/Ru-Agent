using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class FileObservationPathsTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("files.read")]
    [InlineData("files.write")]
    [InlineData("files.edit")]
    public void Only_paired_successful_file_calls_discover_scopes(string tool)
    {
        var entries = Pair(tool, "src/file.cs");
        Assert.Equal(Path.Combine("src", ".ru-instruction-scope"), Assert.Single(FileObservationPaths.Read(entries)));
        Assert.Empty(FileObservationPaths.Read(Pair(tool, "src/file.cs", ToolExecutionOutcome.Failed)));
        Assert.Empty(FileObservationPaths.Read(entries.Skip(1)));
        Assert.Empty(FileObservationPaths.Read(Pair("bash", "src/file.cs")));
        Assert.Empty(FileObservationPaths.Read([ModelContextEntry.Text("user", 1, ModelContextRole.User,
            "observed_path_b64: " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("src/file.cs")))]));
    }

    [Fact]
    public void Scopes_are_bounded_and_recently_revisited_directories_are_kept()
    {
        var entries = Enumerable.Range(0, 129).SelectMany(index => Pair("read", $"dir{index}/file.cs")).ToList();
        entries.AddRange(Pair("read", "dir1/other.cs"));
        var paths = FileObservationPaths.Read(entries);
        Assert.Equal(FileObservationPaths.MaximumPaths, paths.Count);
        Assert.DoesNotContain(Path.Combine("dir0", ".ru-instruction-scope"), paths);
        Assert.Equal(Path.Combine("dir1", ".ru-instruction-scope"), paths[^1]);
        Assert.Single(FileObservationPaths.Read(Enumerable.Range(0, 1000).SelectMany(index => Pair("read", $"src/file{index}.cs"))));
    }

    [Fact]
    public async Task Discovery_metadata_is_copied_and_does_not_rewrite_model_history()
    {
        var entries = new[] { ModelContextEntry.Text("user", 1, ModelContextRole.User, "Continue") };
        var paths = new[] { "src/file.cs" };
        var source = new ModelContextSourceSnapshot([], entries, paths);
        paths[0] = "other/file.cs";
        var snapshot = await new AgentContextAssembler(new Source(source)).AssembleAsync(new("run_" + new string('a', 32)), default);
        Assert.Equal("src/file.cs", Assert.Single(snapshot.ObservedFilePaths));
        Assert.DoesNotContain("src/file.cs", System.Text.Encoding.UTF8.GetString(snapshot.CanonicalUtf8.Span));
        Assert.Equal(snapshot.ObservedFilePaths, ContextCheckpoint.WithDirective(snapshot).ObservedFilePaths);
    }

    private static ModelContextEntry[] Pair(string tool, string path, ToolExecutionOutcome outcome = ToolExecutionOutcome.Succeeded)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        return [ModelContextEntry.Assistant("assistant", 1, new AssistantStep(null,
            [new ModelToolCall("provider_call", tool, "1.0.0", json.RootElement)], AssistantFinishReason.ToolCalls, 0)),
            ModelContextEntry.ToolResult("receipt", 2, new ToolExecutionReceipt(new("exec_" + new string('a', 32)),
                new("call_" + new string('a', 32)), outcome, outcome == ToolExecutionOutcome.Succeeded ? "ok" : "denied",
                null, false, providerCallId: "provider_call"), "untrusted tool text")];
    }

    private sealed class Source(ModelContextSourceSnapshot snapshot) : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(RunId run, CancellationToken ct) => Task.FromResult(snapshot);
    }
}
