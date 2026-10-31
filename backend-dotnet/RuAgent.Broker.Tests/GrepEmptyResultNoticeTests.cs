using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// An empty grep result explains itself only when the scan really covered the
/// search. A partial scan keeps scan_incomplete/excluded_by_size as the account
/// and must not claim that no matches exist.
/// </summary>
public sealed class GrepEmptyResultNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-grep-notice-" + Guid.NewGuid().ToString("N"));

    public GrepEmptyResultNoticeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Empty_complete_scan_carries_a_no_matches_notice()
    {
        Write("a.cs", "alpha");
        Write("b.cs", "beta");

        var result = await Search(new JsonObject { ["query"] = "needle" });

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.False(result["scan_incomplete"]!.GetValue<bool>());
        Assert.Equal(0, result["excluded_by_size"]!.GetValue<int>());
        Assert.Equal("no matches were found for the query.", result["notice"]!.GetValue<string>());
    }

    [Fact]
    public async Task Matches_carry_no_notice()
    {
        Write("a.cs", "needle");

        var result = await Search(new JsonObject { ["query"] = "needle" });

        Assert.Equal(1, result["count"]!.GetValue<int>());
        Assert.False(result.ContainsKey("notice"));
    }

    [Fact]
    public async Task Size_excluded_scan_reports_coverage_instead_of_a_no_matches_notice()
    {
        Write("empty.cs", "alpha");
        Write("large.cs", new string('x', FileToolLimits.MaxSearchFileSize) + "needle");

        var result = await Search(new JsonObject { ["query"] = "needle" });

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.True(result["scan_incomplete"]!.GetValue<bool>());
        Assert.Equal(1, result["excluded_by_size"]!.GetValue<int>());
        Assert.False(result.ContainsKey("notice"));
    }

    private Task<JsonObject> Search(JsonObject arguments) =>
        new FileSearchAdapter(_root).ExecuteAsync(arguments, CancellationToken.None);

    private void Write(string path, string text) => File.WriteAllText(Path.Combine(_root, path), text);
}
