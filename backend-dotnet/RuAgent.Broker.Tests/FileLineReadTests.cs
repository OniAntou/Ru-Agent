using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileLineReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-line-read-" + Guid.NewGuid().ToString("N"));
    public FileLineReadTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Pages_by_one_based_lines_preserving_unicode_and_newlines()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "đầu\r\n🌊 giữa\r\ncuối");
        var page = await new FileReadAdapter(_root).ExecuteLinesAsync(new JsonObject
            { ["path"] = "a.txt", ["start_line"] = 2, ["line_count"] = 1 }, default);
        Assert.Equal("🌊 giữa\r\n", page["content"]!.GetValue<string>());
        Assert.Equal(3, page["next_line"]!.GetValue<int>());
        Assert.True(page["has_more"]!.GetValue<bool>());
        var last = await new FileReadAdapter(_root).ExecuteLinesAsync(new JsonObject
            { ["path"] = "a.txt", ["start_line"] = 3 }, default);
        Assert.Equal("cuối", last["content"]!.GetValue<string>());
        Assert.False(last["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Empty_file_is_a_successful_empty_page()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        var page = await new FileReadAdapter(_root).ExecuteLinesAsync(new JsonObject { ["path"] = "a.txt" }, default);
        Assert.Equal(0, page["line_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Too_long_line_is_named_instead_of_skipping_unreturned_text()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), new string('x', 100_000));
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await new FileReadAdapter(_root)
            .ExecuteLinesAsync(new JsonObject { ["path"] = "a.txt" }, default));
        Assert.Equal("read_line_too_long", error.Message);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
