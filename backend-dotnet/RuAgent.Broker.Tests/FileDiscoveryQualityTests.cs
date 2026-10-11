using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileDiscoveryQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-discovery-quality-" + Guid.NewGuid().ToString("N"));
    public FileDiscoveryQualityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("**/*.cs")]
    [InlineData("**\\*.cs")]
    public async Task Recursive_globs_include_root_and_nested_files_in_both_tools(string pattern)
    {
        Write("root.cs", "needle");
        Write("src/nested.cs", "needle");
        Write("src/deeper/last.cs", "needle");
        var glob = new FileGlobAdapter(_root).Execute(new JsonObject { ["pattern"] = pattern });
        Assert.Equal(3, glob["count"]!.GetValue<int>());
        var grep = await Search(new JsonObject { ["query"] = "needle", ["file_glob"] = pattern });
        Assert.Equal(3, grep["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Literal_glob_text_does_not_turn_into_a_wildcard()
    {
        Write("___DOUBLESTAR___.cs", "needle");
        Write("other.cs", "needle");
        var result = await Search(new JsonObject { ["query"] = "needle", ["file_glob"] = "___DOUBLESTAR___.cs" });
        Assert.Equal(1, result["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Long_line_preview_contains_the_match_and_names_its_column()
    {
        Write("long.cs", new string('x', 900) + "needle" + new string('y', 900));
        var result = await Search(new JsonObject { ["query"] = "needle" });
        var match = Assert.Single(result["results"]!.AsArray())!;
        Assert.Contains("needle", match["preview"]!.GetValue<string>());
        Assert.Equal(901, match["column"]!.GetValue<int>());
        Assert.True(match["preview_column"]!.GetValue<int>() > 1);
        Assert.True(match["preview"]!.GetValue<string>().Length <= 512);
    }

    [Fact]
    public async Task Exact_final_page_does_not_claim_more_results()
    {
        Write("a.cs", "needle\nneedle\nneedle\nneedle");
        var result = await Search(new JsonObject { ["query"] = "needle", ["max_results"] = 2, ["offset"] = 2 });
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.Null(result["next_offset"]);
    }

    [Fact]
    public async Task Lookahead_cursor_reaches_the_remaining_matches()
    {
        Write("a.cs", "needle\nneedle\nneedle");
        var first = await Search(new JsonObject { ["query"] = "needle", ["max_results"] = 2 });
        Assert.Equal(2, first["next_offset"]!.GetValue<int>());
        var last = await Search(new JsonObject { ["query"] = "needle", ["max_results"] = 2, ["offset"] = first["next_offset"]!.GetValue<int>() });
        Assert.Equal(3, Assert.Single(last["results"]!.AsArray())!["line"]!.GetValue<int>());
        Assert.False(last["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Small_page_stops_scanning_later_files_after_ordered_lookahead()
    {
        for (var index = 0; index < 12; index++) Write($"{index:D2}.cs", "needle");
        var result = await Search(new JsonObject { ["query"] = "needle", ["max_results"] = 1 });
        Assert.Equal("00.cs", Assert.Single(result["results"]!.AsArray())!["path"]!.GetValue<string>());
        Assert.True(result["scanned_files"]!.GetValue<int>() < 12);
        Assert.Equal(12, result["discovered_files"]!.GetValue<int>());
        Assert.False(result["scan_incomplete"]!.GetValue<bool>());
    }

    private Task<JsonObject> Search(JsonObject arguments) => new FileSearchAdapter(_root).ExecuteAsync(arguments, CancellationToken.None);
    private void Write(string path, string text)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }
}
