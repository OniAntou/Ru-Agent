using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileReadAdapterTests : IDisposable
{
    private const int AllocationCeiling = 1024 * 1024;
    private readonly string _root;
    private readonly WorkspacePathGuard _workspace = null!;
    private readonly FileTools _files;
    private readonly FileReadAdapter _adapter;

    public FileReadAdapterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new WorkspacePathGuard(_root);
        _files = new FileTools(_workspace);
        _adapter = new FileReadAdapter(_root);
    }

    [Fact]
    public void Read_allocates_only_the_requested_window_for_a_large_file()
    {
        File.WriteAllText(FullPath("warmup.txt"), "warmup");
        _ = _files.Read("warmup.txt", maxBytes: 16);
        using (var stream = File.Create(FullPath("large.bin")))
        {
            stream.SetLength(16L * 1024 * 1024);
        }

        const int maxBytes = 4 * 1024;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _files.Read("large.bin", maxBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(maxBytes, Encoding.UTF8.GetByteCount(result["content"]!.GetValue<string>()));
        Assert.True(
            allocated < AllocationCeiling,
            $"Bounded read allocated {allocated} bytes for a {maxBytes}-byte window.");
    }

    [Fact]
    public void Read_offset_beyond_eof_returns_empty_not_truncated()
    {
        File.WriteAllText(FullPath("small.txt"), "hello");
        var args = new JsonObject { ["path"] = "small.txt", ["offset"] = 100, ["limit"] = 10 };
        var result = _adapter.Execute(args);
        Assert.Equal(string.Empty, result["content"]!.GetValue<string>());
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.Equal(0, result["bytes_read"]!.GetValue<int>());
        Assert.Equal(5, result["total_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void Read_truncated_flag_and_counts_are_bounded()
    {
        File.WriteAllText(FullPath("truncate.txt"), new string('a', 5000));
        var args = new JsonObject { ["path"] = "truncate.txt", ["offset"] = 0, ["limit"] = 100 };
        var result = _adapter.Execute(args);
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(100, result["bytes_read"]!.GetValue<int>());
        Assert.Equal(5000, result["total_bytes"]!.GetValue<long>());
        Assert.Equal(100, Encoding.UTF8.GetByteCount(result["content"]!.GetValue<string>()));
    }

    [Fact]
    public void Read_utf8_boundary_does_not_split_mid_codepoint_and_marks_truncated()
    {
        // File contains 'a' + 4-byte emoji + 'b' = 6 bytes: 61 F0 9F 98 80 62
        var content = "a\U0001F600b";
        File.WriteAllText(FullPath("utf8.txt"), content, new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(FullPath("utf8.txt"));
        Assert.Equal(6, bytes.Length);

        // Limit 3 cuts inside emoji (61 F0 9F) -> incomplete, should truncate before emoji
        var args1 = new JsonObject { ["path"] = "utf8.txt", ["offset"] = 0, ["limit"] = 3 };
        var r1 = _adapter.Execute(args1);
        Assert.Equal("a", r1["content"]!.GetValue<string>());
        Assert.True(r1["truncated"]!.GetValue<bool>());
        Assert.Equal(1, r1["bytes_read"]!.GetValue<int>());

        // Limit 5 includes complete emoji (61 F0 9F 98 80) -> valid "a😀"
        var args2 = new JsonObject { ["path"] = "utf8.txt", ["offset"] = 0, ["limit"] = 5 };
        var r2 = _adapter.Execute(args2);
        Assert.Equal("a\U0001F600", r2["content"]!.GetValue<string>());
        Assert.True(r2["truncated"]!.GetValue<bool>());
        Assert.Equal(5, r2["bytes_read"]!.GetValue<int>());

        // Limit 6 includes all -> not truncated
        var args3 = new JsonObject { ["path"] = "utf8.txt", ["offset"] = 0, ["limit"] = 6 };
        var r3 = _adapter.Execute(args3);
        Assert.Equal(content, r3["content"]!.GetValue<string>());
        Assert.False(r3["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_respects_max_bytes_cap_of_256KiB()
    {
        var schema = RuAgent.Protocol.AgentModelToolContractCatalog.Get("read", "1.0.0").InputSchema;
        // limit 300 KiB should be rejected by schema
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(schema, new JsonObject { ["path"] = "a.txt", ["limit"] = 300 * 1024 }));
        // valid limit passes
        ToolSchemaValidator.Validate(schema, new JsonObject { ["path"] = "a.txt", ["limit"] = 256 * 1024 });
    }

    [Fact]
    public void Read_large_sparse_file_does_not_allocate_full_size()
    {
        var sparsePath = FullPath("sparse.bin");
        using (var stream = File.Create(sparsePath))
        {
            stream.SetLength(20L * 1024 * 1024);
        }

        const int limit = 1024;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _adapter.Execute(new JsonObject { ["path"] = "sparse.bin", ["offset"] = 0, ["limit"] = limit });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(limit, result["bytes_read"]!.GetValue<int>());
        Assert.True(allocated < AllocationCeiling, $"Sparse read allocated {allocated} bytes for {limit}-byte window");
    }

    [Fact]
    public void Read_protected_path_is_denied()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "secret");
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _adapter.Execute(new JsonObject { ["path"] = ".git/config" }));
        Assert.Contains("protected_path", ex.Message);
    }

    [Fact]
    public void Read_hidden_file_is_allowed()
    {
        File.WriteAllText(FullPath(".hidden.txt"), "hidden content");
        var result = _adapter.Execute(new JsonObject { ["path"] = ".hidden.txt" });
        Assert.Equal("hidden content", result["content"]!.GetValue<string>());
        Assert.False(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_offset_and_limit_streaming_is_bounded()
    {
        var content = new string('x', 10000);
        File.WriteAllText(FullPath("bounded.txt"), content);
        var result = _adapter.Execute(new JsonObject { ["path"] = "bounded.txt", ["offset"] = 5000, ["limit"] = 100 });
        Assert.Equal(100, result["bytes_read"]!.GetValue<int>());
        Assert.Equal(10000, result["total_bytes"]!.GetValue<long>());
        Assert.Equal(100, result["content"]!.GetValue<string>().Length);
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_returns_expected_sha256_hash()
    {
        var content = "test sha256 content";
        File.WriteAllText(FullPath("sha.txt"), content, Encoding.UTF8);
        var expectedHash = FileHashService.GetCurrentHash(FullPath("sha.txt"));
        var result = _adapter.Execute(new JsonObject { ["path"] = "sha.txt" });
        Assert.Equal(expectedHash, result["sha256"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result["sha256"]?.GetValue<string>()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FullPath(string relative) => Path.Combine(_root, relative);
}
