using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tests.Support;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
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
    public void Read_tells_the_model_where_to_continue_instead_of_ending_on_a_bare_flag()
    {
        File.WriteAllText(FullPath("truncate.txt"), new string('a', 5000));
        var result = _adapter.Execute(new JsonObject { ["path"] = "truncate.txt", ["limit"] = 100 });

        Assert.Contains("next_offset", result["notice"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(result["estimated_tokens"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Read_explains_an_empty_file_and_an_offset_past_the_end()
    {
        File.WriteAllText(FullPath("empty.txt"), string.Empty);
        File.WriteAllText(FullPath("small.txt"), "hello");

        var empty = _adapter.Execute(new JsonObject { ["path"] = "empty.txt" });
        Assert.Contains("empty", empty["notice"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var past = _adapter.Execute(new JsonObject { ["path"] = "small.txt", ["offset"] = 100, ["limit"] = 10 });
        Assert.Contains("past the end", past["notice"]!.GetValue<string>(), StringComparison.Ordinal);
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
    public void Read_respects_max_bytes_cap_of_4MiB()
    {
        var schema = RuAgent.Protocol.AgentModelToolContractCatalog.Get("read", "1.0.0").InputSchema;
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(schema, new JsonObject { ["path"] = "a.txt", ["limit"] = FileToolLimits.MaxReadBytes + 1 }));
        // valid limit passes
        ToolSchemaValidator.Validate(schema, new JsonObject { ["path"] = "a.txt", ["limit"] = FileToolLimits.MaxReadBytes });
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
    public void Read_protected_path_is_denied_without_the_full_access_flag()
    {
        File.WriteAllText(FullPath("runtime_descriptor.json"), "secret");
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _adapter.Execute(new JsonObject { ["path"] = "runtime_descriptor.json" }));
        Assert.Contains("protected_path", ex.Message);
    }

    [Fact]
    public void Read_protected_path_is_allowed_when_the_tool_set_opens_them()
    {
        // full_access builds its tool sets with allowProtectedPaths: true, so a
        // catalog-protected name resolves like any other target in the root.
        File.WriteAllText(FullPath("runtime_descriptor.json"), "secret");
        var adapter = new FileReadAdapter(_root, allowProtectedPaths: true);

        var result = adapter.Execute(new JsonObject { ["path"] = "runtime_descriptor.json" });

        Assert.Equal("secret", result["content"]!.GetValue<string>());
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

    [Fact]
    public async Task Complete_read_hash_matches_the_returned_content_during_concurrent_replacement()
    {
        var path = FullPath("race.txt");
        await File.WriteAllTextAsync(path, new string('A', 64), new UTF8Encoding(false));
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var values = new[] { new string('A', 64), new string('B', 64) };
            for (var index = 0; index < 5_000 && !stop.IsCancellationRequested; index++)
            {
                try
                {
                    await File.WriteAllTextAsync(path, values[index % values.Length], new UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
            }
        });

        var mismatch = false;
        try
        {
            for (var attempt = 0; attempt < 2_000 && !mismatch; attempt++)
            {
                try
                {
                    var result = _adapter.Execute(new JsonObject { ["path"] = "race.txt" });
                    var content = result["content"]!.GetValue<string>();
                    mismatch = !result["truncated"]!.GetValue<bool>()
                        && result["sha256"]!.GetValue<string>() != FileHashService.ComputeStringHash(content);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }

        Assert.False(mismatch, "A complete read must report the SHA-256 for the content it returned.");
    }

    /// <summary>
    /// An NTFS hard link is reachable inside the workspace while the bytes live
    /// outside it, and it carries no reparse point, so canonicalization cannot see
    /// it: the resolver reports an ordinary in-workspace file. Reading it would
    /// hand out the outside file's contents with no approval, which is the one
    /// hole the name-based boundary cannot close on its own. The guard refuses the
    /// ambiguous name instead.
    /// </summary>
    [Fact]
    public void Read_denies_a_hard_link_that_names_a_file_outside_the_workspace()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "ru-read-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            var secret = Path.Combine(outsideDir, "secret.env");
            File.WriteAllText(secret, "API_KEY=SUPER_SECRET");
            var link = FullPath("leak.txt");
            if (!TryCreateHardLink(link, secret))
            {
                return; // hard links unavailable on this volume; nothing to assert
            }

            // The link is a plausible in-workspace path with no visible marker.
            Assert.Null(new FileInfo(link).LinkTarget);
            Assert.Equal("API_KEY=SUPER_SECRET", File.ReadAllText(link));

            var error = Assert.Throws<UnauthorizedAccessException>(
                () => _adapter.Execute(new JsonObject { ["path"] = "leak.txt" }));
            Assert.Equal("hard_link_not_allowed", error.Message);
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Read_still_allows_an_ordinary_single_name_file()
    {
        File.WriteAllText(FullPath("single.txt"), "one name");

        var result = _adapter.Execute(new JsonObject { ["path"] = "single.txt" });

        Assert.Equal("one name", result["content"]!.GetValue<string>());
    }

    [Fact]
    public void Open_handle_must_still_name_the_authorized_final_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var openedPath = FullPath("opened.txt");
        var expectedPath = FullPath("authorized.txt");
        File.WriteAllText(openedPath, "opened");
        File.WriteAllText(expectedPath, "authorized");
        using var stream = File.OpenRead(openedPath);

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, expectedPath));

        Assert.Equal("file_identity_changed", error.Message);
    }

    internal static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /H \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(10_000);
        return process.ExitCode == 0 && File.Exists(linkPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FullPath(string relative) => Path.Combine(_root, relative);
}
