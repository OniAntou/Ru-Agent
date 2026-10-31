using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileListAdapterTests : IDisposable
{
    private readonly string _root;
    private readonly FileListAdapter _list;

    public FileListAdapterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _list = new FileListAdapter(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void List_empty_directory_reports_notice()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var result = _list.Execute(new JsonObject { ["path"] = "empty" });

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.Empty(result["entries"]!.AsArray());
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.Equal(
            "the directory exists but is empty (0 entries).",
            result["notice"]!.GetValue<string>());
    }

    [Fact]
    public void List_non_empty_directory_has_no_notice()
    {
        Directory.CreateDirectory(Path.Combine(_root, "filled"));
        File.WriteAllText(Path.Combine(_root, "filled", "a.txt"), "a");

        var result = _list.Execute(new JsonObject { ["path"] = "filled" });

        Assert.Equal(1, result["count"]!.GetValue<int>());
        Assert.Null(result["notice"]);
    }

    [Fact]
    public void List_directory_with_only_skipped_entries_is_not_called_empty()
    {
        // A directory whose only entry is skipped as protected still has an entry,
        // so it is not reported as empty; "skipped" already explains the empty list.
        Directory.CreateDirectory(Path.Combine(_root, "protected_only"));
        File.WriteAllText(Path.Combine(_root, "protected_only", "harness_key"), "x");

        var result = _list.Execute(new JsonObject { ["path"] = "protected_only" });

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.Equal(1, result["skipped"]!.GetValue<int>());
        Assert.Null(result["notice"]);
    }
}
