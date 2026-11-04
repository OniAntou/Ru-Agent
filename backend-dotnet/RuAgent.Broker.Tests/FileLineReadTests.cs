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
    public async Task Missing_files_and_directories_return_named_read_errors()
    {
        var adapter = new FileReadAdapter(_root);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(async () => await adapter.ExecuteLinesAsync(
            new JsonObject { ["path"] = "missing.txt" }, default));
        var directory = await Assert.ThrowsAsync<IOException>(async () => await adapter.ExecuteLinesAsync(
            new JsonObject { ["path"] = "." }, default));

        Assert.Equal("file_not_found", missing.Message);
        Assert.Equal("is_directory", directory.Message);
    }

    [Fact]
    public async Task Protected_paths_remain_denied_in_line_mode()
    {
        File.WriteAllText(Path.Combine(_root, "runtime_descriptor.json"), "protected");

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await new FileReadAdapter(_root).ExecuteLinesAsync(
                new JsonObject { ["path"] = "runtime_descriptor.json" }, default));

        Assert.Equal("protected_path", error.Message);
    }

    [Fact]
    public async Task Line_mode_denies_hard_links_to_files_outside_the_workspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-line-read-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var secret = Path.Combine(outside, "secret.txt");
            File.WriteAllText(secret, "outside content");
            var link = Path.Combine(_root, "linked.txt");
            if (!FileReadAdapterTests.TryCreateHardLink(link, secret)) return;

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await new FileReadAdapter(_root).ExecuteLinesAsync(
                    new JsonObject { ["path"] = "linked.txt" }, default));

            Assert.Equal("hard_link_not_allowed", error.Message);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Line_mode_rejects_a_handle_that_changed_after_workspace_resolution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var inside = Path.Combine(_root, "target.txt");
        var outside = Path.Combine(Path.GetTempPath(), "ru-line-read-race-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(inside, "authorized content");
        File.WriteAllText(outside, "outside content");
        try
        {
            var adapter = new FileReadAdapter(_root, _ => File.OpenRead(outside));

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await adapter.ExecuteLinesAsync(new JsonObject { ["path"] = "target.txt" }, default));

            Assert.Equal("file_identity_changed", error.Message);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Line_mode_allows_the_resolved_file_handle_from_its_opener()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "authorized content");
        var adapter = new FileReadAdapter(_root, resolvedPath => File.OpenRead(resolvedPath));

        var page = await adapter.ExecuteLinesAsync(new JsonObject { ["path"] = "target.txt" }, default);

        Assert.Equal("authorized content", page["content"]!.GetValue<string>());
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
