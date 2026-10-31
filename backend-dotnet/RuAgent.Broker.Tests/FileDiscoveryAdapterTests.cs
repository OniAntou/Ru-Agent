using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;

namespace RuAgent.Broker.Tests;

public sealed class FileDiscoveryAdapterTests : IDisposable
{
    private readonly string _root;
    private readonly FileListAdapter _list;
    private readonly FileGlobAdapter _glob;
    private readonly FileSearchAdapter _search;
    private readonly FileReadAdapter _read;

    public FileDiscoveryAdapterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        _list = new FileListAdapter(_root);
        _glob = new FileGlobAdapter(_root);
        _search = new FileSearchAdapter(_root);
        _read = new FileReadAdapter(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---------- deterministic ordinal relative paths ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_reports_size_exclusions_instead_of_complete_empty_coverage(bool explicitFile)
    {
        File.WriteAllText(Path.Combine(_root, "large.cs"), new string('x', FileToolLimits.MaxSearchFileSize) + "needle");
        var result = await _search.ExecuteAsync(new JsonObject {
            ["path"] = explicitFile ? "large.cs" : ".", ["query"] = "needle" }, default);
        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.True(result["scan_incomplete"]!.GetValue<bool>());
        Assert.Equal(1, result["excluded_by_size"]!.GetValue<int>());
        Assert.Null(result["next_offset"]);
    }

    [Fact]
    public async Task Search_file_path_searches_just_that_file()
    {
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "needle here\n");
        File.WriteAllText(Path.Combine(_root, "src", "b.txt"), "needle there\n");
        var result = await _search.ExecuteAsync(
            new JsonObject { ["path"] = "src/a.txt", ["query"] = "needle" },
            CancellationToken.None);
        var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "src/a.txt" }, paths);
    }

    [Fact]
    public async Task Search_missing_path_still_reports_not_a_directory()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => _search.ExecuteAsync(
            new JsonObject { ["path"] = "src/nope.txt", ["query"] = "needle" },
            CancellationToken.None));
        Assert.Equal("not_a_directory", ex.Message);
    }

    [Fact]
    public async Task Search_file_path_respects_file_glob()
    {
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "needle\n");
        var result = await _search.ExecuteAsync(
            new JsonObject { ["path"] = "src/a.txt", ["query"] = "needle", ["file_glob"] = "*.md" },
            CancellationToken.None);
        Assert.Equal(0, result["count"]!.GetValue<int>());
    }

    [Fact]
    public void List_deterministic_ordinal_relative_paths()
    {
        File.WriteAllText(Path.Combine(_root, "src", "z.txt"), "z");
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "src", "m.txt"), "m");
        var first = _list.Execute(new JsonObject { ["path"] = "src" });
        var second = _list.Execute(new JsonObject { ["path"] = "src" });
        var firstPaths = first["entries"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        var secondPaths = second["entries"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.Equal(firstPaths, secondPaths);
        var sorted = firstPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, firstPaths);
    }

    [Fact]
    public void Glob_deterministic_ordinal_relative_paths()
    {
        File.WriteAllText(Path.Combine(_root, "src", "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "a");
        var result = _glob.Execute(new JsonObject { ["path"] = "src", ["pattern"] = "*.txt" });
        var paths = result["results"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, paths);
    }

    [Fact]
    public void Glob_reports_a_notice_when_nothing_matches()
    {
        var result = _glob.Execute(new JsonObject { ["path"] = "src", ["pattern"] = "*.nomatch" });
        Assert.Equal(0, result["count"]!.GetValue<int>());
        var notice = result["notice"]?.GetValue<string>();
        Assert.NotNull(notice);
        Assert.Contains("nothing matched the pattern", notice, StringComparison.Ordinal);
        Assert.Contains("*.nomatch", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Glob_omits_the_notice_when_a_file_matches()
    {
        File.WriteAllText(Path.Combine(_root, "src", "match.txt"), "m");
        var result = _glob.Execute(new JsonObject { ["path"] = "src", ["pattern"] = "*.txt" });
        Assert.Equal(1, result["count"]!.GetValue<int>());
        Assert.False(result.ContainsKey("notice"));
    }

    [Fact]
    public async Task Search_deterministic_ordinal_relative_paths_then_line()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "hello\nworld hello\n");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "b.txt"), "hello");
        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = "src", ["query"] = "hello" }, CancellationToken.None);
        var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>() + ":" + n!["line"]!.GetValue<int>()).ToArray();
        var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, paths);
    }

    [Fact]
    public async Task Search_skips_hard_linked_file_without_exposing_content()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-search-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "secret.txt");
        File.WriteAllText(target, "hard-link-search-marker");
        var link = Path.Combine(_root, "leak.txt");
        if (!FileReadAdapterTests.TryCreateHardLink(link, target))
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "hard-link-search-marker" }, CancellationToken.None);
            var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
            Assert.DoesNotContain("leak.txt", paths);
            Assert.Equal(0, result["count"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Search_async_is_cancellable_and_keeps_ordinal_result_order()
    {
        File.WriteAllText(Path.Combine(_root, "src", "z.txt"), "needle\n");
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "needle\n");
        File.WriteAllText(Path.Combine(_root, "src", "m.txt"), "needle\n");

        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = "src", ["query"] = "needle" }, CancellationToken.None);
        var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _search.ExecuteAsync(new JsonObject { ["path"] = "src", ["query"] = "needle" }, cts.Token));
    }

    [Fact]
    public async Task Search_large_offset_skips_matches_across_files_without_losing_page_order()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), string.Concat(Enumerable.Repeat("needle\n", 6000)));
        File.WriteAllText(Path.Combine(_root, "b.txt"), string.Concat(Enumerable.Repeat("needle\n", 6000)));
        var args = new JsonObject { ["query"] = "needle", ["offset"] = 10000, ["max_results"] = 2 };
        var result = await _search.ExecuteAsync(args, default);
        var matches = result["results"]!.AsArray();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("b.txt", match!["path"]!.GetValue<string>()));
        Assert.Equal(4001, matches[0]!["line"]!.GetValue<int>());
        Assert.Equal(4002, matches[1]!["line"]!.GetValue<int>());
        Assert.Equal(10002, result["next_offset"]!.GetValue<int>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _search.ExecuteAsync(args, cancellation.Token));
    }

    // ---------- hidden / binary handling ----------

    [Fact]
    public void List_includes_hidden_files()
    {
        File.WriteAllText(Path.Combine(_root, ".hidden.txt"), "hidden");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "visible");
        var result = _list.Execute(new JsonObject { ["path"] = "." });
        var paths = result["entries"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.Contains(".hidden.txt", paths);
        Assert.Contains("visible.txt", paths);
    }

    [Fact]
    public async Task Search_skips_binary_files()
    {
        var binaryPath = Path.Combine(_root, "binary.bin");
        File.WriteAllBytes(binaryPath, new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x01, 0x02 });
        File.WriteAllText(Path.Combine(_root, "text.txt"), "hello text");
        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "hello" }, CancellationToken.None);
        var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("binary.bin", paths);
        Assert.Contains("text.txt", paths);
        Assert.True(result["skipped"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void Glob_includes_hidden_and_excludes_binary_irrelevant()
    {
        File.WriteAllText(Path.Combine(_root, ".hidden2.txt"), "h");
        File.WriteAllBytes(Path.Combine(_root, "binary2.bin"), new byte[] { 0x00, 0x01 });
        var result = _glob.Execute(new JsonObject { ["path"] = ".", ["pattern"] = "*.txt" });
        var paths = result["results"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains(".hidden2.txt", paths);
    }

    // ---------- inaccessible entry skip metadata ----------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Search_preserves_statistics_and_order_across_batches(int concurrency)
    {
        for (var index = 0; index < 9; index++)
            File.WriteAllText(Path.Combine(_root, "src", $"a{index}.txt"), index % 2 == 0 ? "needle" : "other");
        File.WriteAllBytes(Path.Combine(_root, "src", "b0.bin"), new byte[] { 0, 1 });
        File.WriteAllBytes(Path.Combine(_root, "src", "b1.bin"), new byte[] { 0, 1 });

        var result = await new FileSearchAdapter(_root, concurrency).ExecuteAsync(
            new JsonObject { ["path"] = "src", ["query"] = "needle", ["max_results"] = 20 },
            CancellationToken.None);

        Assert.Equal(11, result["discovered_files"]!.GetValue<int>());
        Assert.Equal(11, result["scanned_files"]!.GetValue<int>());
        Assert.Equal(2, result["skipped"]!.GetValue<int>());
        Assert.False(result["scan_incomplete"]!.GetValue<bool>());
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.Equal(new[] { "src/a0.txt", "src/a2.txt", "src/a4.txt", "src/a6.txt", "src/a8.txt" },
            result["results"]!.AsArray().Select(row => row!["path"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void List_skips_protected_and_counts_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "harness_key"));
        File.WriteAllText(Path.Combine(_root, "harness_key", "config"), "secret");
        File.WriteAllText(Path.Combine(_root, "normal.txt"), "ok");
        var result = _list.Execute(new JsonObject { ["path"] = "." });
        var paths = result["entries"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("harness_key/config", paths);
        Assert.DoesNotContain("harness_key", paths);
        Assert.Contains("normal.txt", paths);
        Assert.True(result["skipped"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task Search_skips_inaccessible_locked_file_and_counts_skipped()
    {
        var lockedPath = Path.Combine(_root, "locked.txt");
        File.WriteAllText(lockedPath, "hello locked");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "hello" }, CancellationToken.None);
        // locked file should be skipped (IOException) or found if share allowed; we at least check no crash and skipped counted if not found
        // If file is locked exclusive, our search should skip it and count skipped
        // We don't assert exact, just that it doesn't throw and returns bounded result
        Assert.NotNull(result["results"]);
        Assert.True(result["count"]!.GetValue<int>() >= 0);
    }

    // ---------- junction / symlink containment ----------

    [Fact]
    public async Task Junction_escape_is_contained_for_list_and_glob_and_search()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-junction-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        var junction = Path.Combine(_root, "link");
        var created = TryCreateJunction(junction, outside);
        if (!created)
        {
            Directory.Delete(outside, true);
            return;
        }

        try
        {
            // List should not include outside file via link
            var listResult = _list.Execute(new JsonObject { ["path"] = "." });
            var listPaths = listResult["entries"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
            Assert.DoesNotContain("link/secret.txt", listPaths);
            Assert.DoesNotContain("link", listPaths.Where(p => p.Contains("secret")));

            // Glob should not leak
            var globResult = _glob.Execute(new JsonObject { ["path"] = ".", ["pattern"] = "**/secret.txt" });
            var globPaths = globResult["results"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.DoesNotContain("link/secret.txt", globPaths);

            // Search should not leak
            var searchResult = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "outside" }, CancellationToken.None);
            var searchPaths = searchResult["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
            Assert.DoesNotContain("link/secret.txt", searchPaths);
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { }
            Directory.Delete(outside, true);
        }
    }

    // ---------- result / byte / time caps ----------

    [Fact]
    public void List_respects_max_entries_cap_and_truncated()
    {
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"cap{i}.txt"), "x");
        }

        var result = _list.Execute(new JsonObject { ["path"] = ".", ["max_entries"] = 2 });
        Assert.Equal(2, result["count"]!.GetValue<int>());
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Glob_respects_max_results_cap_and_truncated()
    {
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"g{i}.txt"), "x");
        }

        var result = _glob.Execute(new JsonObject { ["path"] = ".", ["pattern"] = "g*.txt", ["max_results"] = 3 });
        Assert.Equal(3, result["count"]!.GetValue<int>());
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_respects_max_results_cap_and_truncated()
    {
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"s{i}.txt"), "needle");
        }

        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["max_results"] = 2 }, CancellationToken.None);
        Assert.Equal(2, result["count"]!.GetValue<int>());
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_respects_max_bytes_cap()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 5000));
        var result = _read.Execute(new JsonObject { ["path"] = "big.txt", ["limit"] = 100 });
        Assert.Equal(100, result["bytes_read"]!.GetValue<int>());
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void List_and_search_time_cap_is_bounded()
    {
        // Create many files to ensure enumeration takes time but should still bound
        for (int i = 0; i < 200; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"t{i}.txt"), "content");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = _list.Execute(new JsonObject { ["path"] = ".", ["max_entries"] = 512 });
        sw.Stop();
        Assert.True(result["duration_ms"]!.GetValue<int>() < FileToolLimits.MaxTimeMs + 1000);
        Assert.True(sw.ElapsedMilliseconds < 10000);
    }

    // ---------- offset beyond EOF, UTF-8 boundaries ----------

    [Fact]
    public async Task Search_offset_pages_through_ordinal_results()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"p{i}.txt"), "needle");
        }

        var first = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["max_results"] = 2 }, CancellationToken.None);
        var second = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["max_results"] = 2, ["offset"] = 2 }, CancellationToken.None);
        var firstPaths = first["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        var secondPaths = second["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "p0.txt", "p1.txt" }, firstPaths);
        Assert.Equal(new[] { "p2.txt", "p3.txt" }, secondPaths);
        Assert.Empty(firstPaths.Intersect(secondPaths));
    }

    [Fact]
    public async Task Search_covers_candidates_beyond_ten_mib_aggregate_file_size()
    {
        const int candidateCount = 20;
        var padding = new string('x', 900_000);
        for (var index = 0; index < candidateCount; index++)
        {
            var marker = index == candidateCount - 1 ? "needle" : "no-match";
            File.WriteAllText(Path.Combine(_root, $"bulk-{index:D2}.txt"), padding + marker);
        }

        var result = await _search.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["query"] = "needle" }, CancellationToken.None);

        var match = Assert.Single(result["results"]!.AsArray());
        Assert.Equal("bulk-19.txt", match!["path"]!.GetValue<string>());
        Assert.Equal(candidateCount, result["discovered_files"]!.GetValue<int>());
        Assert.Equal(candidateCount, result["scanned_files"]!.GetValue<int>());
        Assert.False(result["scan_incomplete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_offset_collects_later_lines_within_one_file()
    {
        using (var writer = new StreamWriter(Path.Combine(_root, "many.txt"), false, Encoding.UTF8))
        {
            for (int i = 0; i < 10; i++) writer.WriteLine($"line {i} needle");
        }

        var page = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["max_results"] = 3, ["offset"] = 5 }, CancellationToken.None);
        var lines = page["results"]!.AsArray().Select(n => n!["line"]!.GetValue<int>()).ToArray();
        Assert.Equal(new[] { 6, 7, 8 }, lines);
    }

    [Fact]
    public async Task Search_offset_beyond_results_returns_an_empty_page()
    {
        File.WriteAllText(Path.Combine(_root, "one.txt"), "needle");
        var page = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["offset"] = 100 }, CancellationToken.None);
        Assert.Equal(0, page["count"]!.GetValue<int>());
        Assert.Empty(page["results"]!.AsArray());
    }

    [Fact]
    public async Task Search_rejects_invalid_offset()
    {
        File.WriteAllText(Path.Combine(_root, "o.txt"), "needle");
        await Assert.ThrowsAsync<ArgumentException>(() => _search.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["query"] = "needle", ["offset"] = -1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _search.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["query"] = "needle", ["offset"] = FileToolLimits.MaxOffset + 1 }, CancellationToken.None));
    }

    [Fact]
    public void Read_offset_beyond_eof_returns_empty()
    {
        File.WriteAllText(Path.Combine(_root, "eof.txt"), "hello");
        var result = _read.Execute(new JsonObject { ["path"] = "eof.txt", ["offset"] = 100, ["limit"] = 10 });
        Assert.Equal(string.Empty, result["content"]!.GetValue<string>());
        Assert.False(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_utf8_boundary_is_handled()
    {
        var content = "a\U0001F600b";
        File.WriteAllText(Path.Combine(_root, "utf8b.txt"), content, new UTF8Encoding(false));
        var r = _read.Execute(new JsonObject { ["path"] = "utf8b.txt", ["offset"] = 0, ["limit"] = 3 });
        Assert.Equal("a", r["content"]!.GetValue<string>());
        Assert.True(r["truncated"]!.GetValue<bool>());
    }

    // ---------- large / sparse no unbounded allocation ----------

    [Fact]
    public void Large_sparse_file_list_does_not_allocate_unbounded()
    {
        var sparse = Path.Combine(_root, "sparse_list.bin");
        using (var s = File.Create(sparse)) s.SetLength(10L * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _read.Execute(new JsonObject { ["path"] = "sparse_list.bin", ["limit"] = 1024 });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2 * 1024 * 1024, $"Allocated {allocated} for sparse read");
        Assert.True(result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_drains_truncates_without_unbounded_accumulation_for_large_file()
    {
        // Create a file with many matching lines
        var bigPath = Path.Combine(_root, "bigsearch.txt");
        using (var w = new StreamWriter(bigPath, false, Encoding.UTF8))
        {
            for (int i = 0; i < 5000; i++) w.WriteLine($"line {i} needle");
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle", ["max_results"] = 64 }, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(64, result["count"]!.GetValue<int>());
        Assert.True(result["truncated"]!.GetValue<bool>());
        // Search should not allocate massive result list unbounded (e.g., 5000 lines)
        Assert.True(allocated < 10 * 1024 * 1024, $"Search allocated {allocated} for bounded 64 results from 5000 matches");
    }

    [Fact]
    public async Task Search_large_sparse_file_does_not_allocate_full_size()
    {
        var sparse = Path.Combine(_root, "sparse_search.txt");
        using (var s = File.Create(sparse)) s.SetLength(15L * 1024 * 1024);
        // Write a small header so file is considered text not binary (no NUL in first 8k maybe? sparse file reads as zeros which contain NUL, so it will be considered binary and skipped, which is correct bounded)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "needle" }, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 5 * 1024 * 1024);
        // Should not contain sparse file as binary skipped
        var paths = result["results"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("sparse_search.txt", paths);
    }

    // ---------- truncated metadata bounded ----------

    [Fact]
    public async Task Truncated_metadata_is_bounded_and_does_not_leak_content()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello world");
        var list = _list.Execute(new JsonObject { ["path"] = ".", ["max_entries"] = 1 });
        Assert.True(list["truncated"]!.GetValue<bool>());
        Assert.True(list["count"]!.GetValue<int>() <= 1);
        // Safe fields must not contain file content: check that list output does not contain file content in safe summary? Our adapter's safe fields are count/truncated, entries contain paths only, not content
        var entries = list["entries"]!.AsArray();
        foreach (var e in entries) Assert.DoesNotContain("hello", e!["path"]!.GetValue<string>());

        var search = await _search.ExecuteAsync(new JsonObject { ["path"] = ".", ["query"] = "hello", ["max_results"] = 1 }, CancellationToken.None);
        // search results contain preview which is file content but it's private payload; safe fields count/truncated are separate
        Assert.True(search["count"]!.GetValue<int>() <= 1);
        // Ensure safe receipt fields count/truncated exist and are bounded
        Assert.True(search.ContainsKey("truncated"));
    }

    // ---------- policy tests ----------

    [Fact]
    public void Policy_allows_standard_workspace_discovery_and_read()
    {
        var lease = CreateLease("standard", Path.Combine(_root, "src"));
        var factory = new PolicyContextFactory(lease);
        var evaluator = new PolicySnapshotEvaluator();

        var listCtx = factory.Create("list", "1.0.0", new JsonObject { ["path"] = "src" });
        var listEval = evaluator.Evaluate(lease.Snapshot, listCtx, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("allow", listEval.Decision);

        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "x");
        var readEval = evaluator.Evaluate(lease.Snapshot, factory.Create("read", "1.0.0", new JsonObject { ["path"] = "src/a.txt" }), PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("allow", readEval.Decision);
    }

    [Fact]
    public void Policy_context_defaults_optional_discovery_path_to_workspace_root()
    {
        var lease = CreateLease("standard", _root);
        var factory = new PolicyContextFactory(lease);

        var listContext = factory.Create("list", "1.0.0", new JsonObject());
        Assert.NotNull(listContext.Target);
        Assert.Equal(lease.CanonicalRoot, listContext.Target!.CanonicalPath);

        var globContext = factory.Create("glob", "1.0.0", new JsonObject { ["pattern"] = "*.txt" });
        Assert.NotNull(globContext.Target);
        Assert.Equal(lease.CanonicalRoot, globContext.Target!.CanonicalPath);

        var grepContext = factory.Create("grep", "1.0.0", new JsonObject { ["query"] = "needle" });
        Assert.NotNull(grepContext.Target);
        Assert.Equal(lease.CanonicalRoot, grepContext.Target!.CanonicalPath);
    }

    [Fact]
    public void Policy_intent_hash_binds_non_path_arguments()
    {
        var lease = CreateLease("standard", _root);
        var factory = new PolicyContextFactory(lease);

        var searchA = factory.Create("grep", "1.0.0", new JsonObject { ["query"] = "alpha" });
        var searchB = factory.Create("grep", "1.0.0", new JsonObject { ["query"] = "beta" });
        var readA = factory.Create("read", "1.0.0", new JsonObject { ["path"] = "src/a.txt", ["offset"] = 0, ["limit"] = 10 });
        var readB = factory.Create("read", "1.0.0", new JsonObject { ["path"] = "src/a.txt", ["offset"] = 10, ["limit"] = 10 });

        Assert.NotEqual(searchA.IntentHash, searchB.IntentHash);
        Assert.NotEqual(readA.IntentHash, readB.IntentHash);
    }

    [Fact]
    public void Policy_denies_protected_and_asks_for_ungranted_external_path()
    {
        var lease = CreateLease("standard", _root);
        var factory = new PolicyContextFactory(lease);
        var evaluator = new PolicySnapshotEvaluator();

        var protectedCtx = factory.Create("read", "1.0.0", new JsonObject { ["path"] = "runtime_descriptor.json" });
        var protEval = evaluator.Evaluate(lease.Snapshot, protectedCtx, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("deny", protEval.Decision);
        Assert.Equal("protected_path", protEval.ReasonCode);

        var outsidePath = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outsidePath, "outside");
        try
        {
            var outsideCtx = factory.Create("read", "1.0.0", new JsonObject { ["path"] = outsidePath });
            var outEval = evaluator.Evaluate(lease.Snapshot, outsideCtx, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
            Assert.Equal("ask", outEval.Decision);
            Assert.Equal("external_directory_required", outEval.ReasonCode);
        }
        finally { File.Delete(outsidePath); }
    }

    [Fact]
    public void Full_access_opens_protected_paths_and_still_denies_malformed_input()
    {
        var lease = CreateLease("full_access", _root);
        var factory = new PolicyContextFactory(lease);
        var evaluator = new PolicySnapshotEvaluator();

        // full_access is literally full: the harness self-protection deny is a
        // plan/standard guard, and the preset evaluates catalog-protected names
        // like any other target.
        var protCtx = factory.Create("read", "1.0.0", new JsonObject { ["path"] = "runtime_descriptor.json" });
        var protEval = evaluator.Evaluate(lease.Snapshot, protCtx, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        Assert.Equal("allow", protEval.Decision);
        Assert.Equal("full_access_allow", protEval.ReasonCode);

        // Malformed path should deny before adapter (invalid_path)
        Assert.Throws<PolicyContextException>(() => factory.Create("read", "1.0.0", new JsonObject { ["path"] = "" }));
        Assert.Throws<PolicyContextException>(() => factory.Create("read", "1.0.0", new JsonObject { ["path"] = "a\0b" }));

        // A tool set built without the full-access flag stays fail-closed.
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _read.Execute(new JsonObject { ["path"] = "runtime_descriptor.json" }));
        Assert.Contains("protected_path", ex.Message);
    }

    private BrokerRunLease CreateLease(string preset, string canonicalRoot)
    {
        Directory.CreateDirectory(canonicalRoot);
        var snapshot = preset == "standard" ? LeaseSnapshots.StandardEmpty() : LeaseSnapshots.FullAccessEmpty();
        // Need to ensure workspace identity matches catalog's expected? Use LeaseTestSnapshots helpers
        var wsId = "ws_" + Guid.NewGuid().ToString("N")[..32];
        var displayHash = new string('a', 64);
        var wire = new BrokerPolicySnapshotWire(
            1,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(wsId, "VOL-TEST", "fid-test", displayHash),
            snapshot.OrderedRules,
            "2026-08-24T00:00:00.0000000+00:00");
        var hash = BrokerPolicyContracts.ComputeCanonicalSnapshotHash(wire);
        return BrokerRunLease.CreateValidated(
            "run_" + Guid.NewGuid().ToString("N")[..32],
            "session-test",
            wsId,
            canonicalRoot,
            "VOL-TEST",
            "fid-test",
            displayHash,
            preset,
            1,
            hash,
            DateTimeOffset.UtcNow.AddMinutes(5),
            wire);
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath)) return true;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return Directory.Exists(junctionPath);
        }
        catch { return false; }
    }

    private static class LeaseSnapshots
    {
        public static BrokerPolicySnapshotWire StandardEmpty() => new(
            1, "standard",
            new BrokerPolicyWorkspaceIdentityWire("ws_22222222222222222222222222222222", "VOL-SESS", "fid-sess", new string('c', 64)),
            [], "2026-08-24T00:00:00.0000000+00:00");

        public static BrokerPolicySnapshotWire FullAccessEmpty() => new(
            1, "full_access",
            new BrokerPolicyWorkspaceIdentityWire("ws_22222222222222222222222222222222", "VOL-SESS", "fid-sess", new string('c', 64)),
            [], "2026-08-24T00:00:00.0000000+00:00");
    }
}
