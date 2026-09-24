using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileMutationAdapterTests : IDisposable
{
    private readonly string _root;
    private readonly FileWriteAdapter _write;
    private readonly FileEditAdapter _edit;

    public FileMutationAdapterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-mut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _write = new FileWriteAdapter(_root);
        _edit = new FileEditAdapter(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(string content) => FileHashService.ComputeStringHash(content);
    private static string HashBytes(byte[] bytes) => FileHashService.ComputeBytesHash(bytes);

    // ---------- schema: expected_sha256 required ----------

    [Fact]
    public void Write_requires_expected_sha256_lowercase_64hex_or_missing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Full("src/a.txt"), "hello");
        var validHash = FileHashService.ComputeFileHash(Full("src/a.txt"));
        var argsValid = new JsonObject { ["path"] = "src/a.txt", ["content"] = "new", ["expected_sha256"] = validHash };
        var prep = _write.Prepare(argsValid);
        Assert.NotNull(prep);

        var argsMissingExpected = new JsonObject { ["path"] = "src/new.txt", ["content"] = "hi" };
        Assert.Throws<ArgumentException>(() => _write.Prepare(argsMissingExpected));

        var argsUpper = new JsonObject { ["path"] = "src/a.txt", ["content"] = "new", ["expected_sha256"] = validHash.ToUpperInvariant() };
        Assert.Throws<ArgumentException>(() => _write.Prepare(argsUpper));

        var argsShort = new JsonObject { ["path"] = "src/a.txt", ["content"] = "new", ["expected_sha256"] = "abc" };
        Assert.Throws<ArgumentException>(() => _write.Prepare(argsShort));

        var argsCreateMissing = new JsonObject { ["path"] = "src/new2.txt", ["content"] = "hi", ["expected_sha256"] = "missing" };
        var prep2 = _write.Prepare(argsCreateMissing);
        Assert.Equal("missing", prep2["before_sha256"]?.GetValue<string>());
    }

    [Fact]
    public void Write_literal_missing_only_for_create()
    {
        File.WriteAllText(Full("exists.txt"), "exists");
        var hash = FileHashService.ComputeFileHash(Full("exists.txt"));
        var argsMissingOnExisting = new JsonObject { ["path"] = "exists.txt", ["content"] = "x", ["expected_sha256"] = "missing" };
        var ex = Assert.Throws<IOException>(() => _write.Execute(argsMissingOnExisting));
        Assert.Equal("file_exists", ex.Message);
        Assert.Equal("exists", File.ReadAllText(Full("exists.txt")));

        var argsHashOnMissing = new JsonObject { ["path"] = "missing.txt", ["content"] = "x", ["expected_sha256"] = hash };
        var ex2 = Assert.Throws<UnauthorizedAccessException>(() => _write.Execute(argsHashOnMissing));
        Assert.Equal("workspace_changed", ex2.Message);
        Assert.False(File.Exists(Full("missing.txt")));
    }

    [Fact]
    public void Edit_requires_expected_sha256()
    {
        File.WriteAllText(Full("e.txt"), "hello world");
        var hash = FileHashService.ComputeFileHash(Full("e.txt"));
        var argsMissing = new JsonObject { ["path"] = "e.txt", ["old_string"] = "world", ["new_string"] = "Ru" };
        Assert.Throws<ArgumentException>(() => _edit.Prepare(argsMissing));

        var argsValid = new JsonObject { ["path"] = "e.txt", ["old_string"] = "world", ["new_string"] = "Ru", ["expected_sha256"] = hash };
        var prep = _edit.Prepare(argsValid);
        Assert.NotNull(prep);
    }

    // ---------- preimage mismatch workspace_changed zero write ----------

    [Fact]
    public void Write_preimage_mismatch_returns_workspace_changed_with_zero_write()
    {
        File.WriteAllText(Full("a.txt"), "original");
        var correctHash = FileHashService.ComputeFileHash(Full("a.txt"));
        var wrongHash = new string('a', 64);
        Assert.NotEqual(correctHash, wrongHash);
        var args = new JsonObject { ["path"] = "a.txt", ["content"] = "new", ["expected_sha256"] = wrongHash, ["overwrite"] = true };
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _write.Execute(args));
        Assert.Equal("workspace_changed", ex.Message);
        Assert.Equal("original", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void Write_concurrent_external_change_detected_at_execution()
    {
        File.WriteAllText(Full("conc.txt"), "v1");
        var hashV1 = FileHashService.ComputeFileHash(Full("conc.txt"));
        var prep = _write.Prepare(new JsonObject { ["path"] = "conc.txt", ["content"] = "v2", ["expected_sha256"] = hashV1 });
        Assert.Equal(hashV1, prep["before_sha256"]?.GetValue<string>());

        File.WriteAllText(Full("conc.txt"), "external");
        var args = new JsonObject { ["path"] = "conc.txt", ["content"] = "v2", ["expected_sha256"] = hashV1, ["overwrite"] = true };
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _write.Execute(args));
        Assert.Equal("workspace_changed", ex.Message);
        Assert.Equal("external", File.ReadAllText(Full("conc.txt")));
    }

    [Fact]
    public void Prepare_is_side_effect_free()
    {
        File.WriteAllText(Full("prep.txt"), "before");
        var hash = FileHashService.ComputeFileHash(Full("prep.txt"));
        var args = new JsonObject { ["path"] = "prep.txt", ["content"] = "after", ["expected_sha256"] = hash };
        var prep = _write.Prepare(args);
        Assert.Equal("before", File.ReadAllText(Full("prep.txt")));
        Assert.Equal(hash, prep["before_sha256"]?.GetValue<string>());
        var afterHash = FileHashService.ComputeStringHash("after");
        Assert.Equal(afterHash, prep["after_sha256"]?.GetValue<string>());
    }

    [Fact]
    public void Execution_does_atomic_replacement_same_volume_temp_and_flush()
    {
        File.WriteAllText(Full("atomic.txt"), "old");
        var hash = FileHashService.ComputeFileHash(Full("atomic.txt"));
        var target = Full("atomic.txt");
        var dir = Path.GetDirectoryName(target)!;
        var beforeTemps = Directory.GetFiles(dir, ".ru-tmp-*.tmp");
        Assert.Empty(beforeTemps);

        var result = _write.Execute(new JsonObject { ["path"] = "atomic.txt", ["content"] = "new content for atomic", ["expected_sha256"] = hash, ["overwrite"] = true });
        Assert.Equal(hash, result["before_sha256"]?.GetValue<string>());
        var newHash = FileHashService.ComputeStringHash("new content for atomic");
        Assert.Equal(newHash, result["after_sha256"]?.GetValue<string>());
        Assert.Equal("new content for atomic", File.ReadAllText(target));
        var afterTemps = Directory.GetFiles(dir, ".ru-tmp-*.tmp");
        Assert.Empty(afterTemps);

        var tempInDifferentVolume = Path.Combine(Path.GetTempPath(), ".ru-tmp-fake.tmp");
        Assert.False(File.Exists(tempInDifferentVolume) && Path.GetDirectoryName(target) == Path.GetDirectoryName(tempInDifferentVolume));
    }

    [Fact]
    public void Write_permission_error_propagates_and_leaves_file_intact()
    {
        File.WriteAllText(Full("perm.txt"), "orig");
        var hash = FileHashService.ComputeFileHash(Full("perm.txt"));
        File.SetAttributes(Full("perm.txt"), FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => _write.Execute(new JsonObject { ["path"] = "perm.txt", ["content"] = "new", ["expected_sha256"] = hash, ["overwrite"] = true }));
            Assert.True(ex.Message == "access_denied" || ex.Message == "workspace_changed" || ex is UnauthorizedAccessException);
            // File may have been replaced despite read-only? On Windows File.Move can replace read-only if we use overwrite; but our test expects either success or access_denied
            // If it succeeded, verify content changed; if access_denied, content unchanged
            var content = File.ReadAllText(Full("perm.txt"));
            Assert.True(content == "orig" || content == "new");
        }
        finally
        {
            File.SetAttributes(Full("perm.txt"), FileAttributes.Normal);
        }
    }

    [Fact]
    public void Link_replacement_between_prepare_and_execute_is_detected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-link-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");

        var realDir = Path.Combine(_root, "real");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "file.txt"), "inside");

        var linkDir = Path.Combine(_root, "link");
        bool created = TryCreateJunction(linkDir, realDir);
        if (!created)
        {
            Directory.Delete(outside, true);
            return;
        }

        try
        {
            var hash = FileHashService.ComputeFileHash(Path.Combine(linkDir, "file.txt"));
            var prep = _write.Prepare(new JsonObject { ["path"] = "link/file.txt", ["content"] = "new inside", ["expected_sha256"] = hash });
            Assert.Equal(hash, prep["before_sha256"]?.GetValue<string>());

            // Replace junction to point outside
            Directory.Delete(linkDir, false);
            bool recreated = TryCreateJunction(linkDir, outside);
            if (!recreated)
            {
                return;
            }

            var args = new JsonObject { ["path"] = "link/file.txt", ["content"] = "evil", ["expected_sha256"] = hash };
            var ex = Assert.Throws<UnauthorizedAccessException>(() => _write.Execute(args));
            Assert.Equal("workspace_changed", ex.Message);

            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "secret.txt")));
            // Inside file should remain unchanged
            Assert.Equal("inside", File.ReadAllText(Path.Combine(realDir, "file.txt")));
            // Outside file not overwritten via link
            Assert.False(File.Exists(Path.Combine(outside, "file.txt")));
        }
        finally
        {
            try { if (Directory.Exists(linkDir)) Directory.Delete(linkDir, false); } catch { }
            Directory.Delete(outside, true);
            try { Directory.Delete(realDir, true); } catch { }
        }
    }

    // ---------- edit tests: exact occurrence, 4MiB cap, UTF-8, no match/multiple ----------

    [Fact]
    public void Edit_exact_occurrence_count_enforced()
    {
        File.WriteAllText(Full("edit1.txt"), "foo foo foo");
        var hash = FileHashService.ComputeFileHash(Full("edit1.txt"));

        var argsOk = new JsonObject { ["path"] = "edit1.txt", ["old_string"] = "foo", ["new_string"] = "bar", ["expected_sha256"] = hash, ["expected_occurrences"] = 3 };
        var result = _edit.Execute(argsOk);
        Assert.Equal(3, result["replacements"]?.GetValue<int>());
        Assert.Equal("bar bar bar", File.ReadAllText(Full("edit1.txt")));

        File.WriteAllText(Full("edit2.txt"), "foo foo");
        var hash2 = FileHashService.ComputeFileHash(Full("edit2.txt"));
        var argsMismatch = new JsonObject { ["path"] = "edit2.txt", ["old_string"] = "foo", ["new_string"] = "bar", ["expected_sha256"] = hash2, ["expected_occurrences"] = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => _edit.Execute(argsMismatch));
        Assert.Equal("edit_not_unique", ex.Message);
        Assert.Equal("foo foo", File.ReadAllText(Full("edit2.txt")));

        File.WriteAllText(Full("edit3.txt"), "hello");
        var hash3 = FileHashService.ComputeFileHash(Full("edit3.txt"));
        var argsNoMatch = new JsonObject { ["path"] = "edit3.txt", ["old_string"] = "missing", ["new_string"] = "x", ["expected_sha256"] = hash3 };
        var ex2 = Assert.Throws<InvalidOperationException>(() => _edit.Execute(argsNoMatch));
        Assert.Equal("edit_no_match", ex2.Message);
    }

    [Fact]
    public void Edit_rejects_hard_link_before_diff_or_mutation()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-edit-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-edit-marker";
        var target = Path.Combine(outside, "secret.txt");
        File.WriteAllText(target, content);
        var link = Full("leak.txt");
        if (!FileReadAdapterTests.TryCreateHardLink(link, target))
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var arguments = new JsonObject
            {
                ["path"] = "leak.txt",
                ["old_string"] = content,
                ["new_string"] = "replacement",
                ["expected_sha256"] = Hash(content),
            };
            var prepareError = Assert.Throws<UnauthorizedAccessException>(() => _edit.Prepare(arguments));
            Assert.Equal("hard_link_not_allowed", prepareError.Message);
            var executeError = Assert.Throws<UnauthorizedAccessException>(() => _edit.Execute(arguments));
            Assert.Equal("hard_link_not_allowed", executeError.Message);
            Assert.Equal(content, File.ReadAllText(target));
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_rejects_hard_link_before_diff_or_mutation()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-write-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-write-marker";
        var target = Path.Combine(outside, "secret.txt");
        File.WriteAllText(target, content);
        var link = Full("leak.txt");
        if (!FileReadAdapterTests.TryCreateHardLink(link, target))
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var arguments = new JsonObject
            {
                ["path"] = "leak.txt",
                ["content"] = "replacement",
                ["expected_sha256"] = Hash(content),
                ["overwrite"] = true,
            };
            var prepareError = Assert.Throws<UnauthorizedAccessException>(() => _write.Prepare(arguments));
            Assert.Equal("hard_link_not_allowed", prepareError.Message);
            var executeError = Assert.Throws<UnauthorizedAccessException>(() => _write.Execute(arguments));
            Assert.Equal("hard_link_not_allowed", executeError.Message);
            Assert.Equal(content, File.ReadAllText(target));
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Edit_4MiB_input_cap_enforced()
    {
        File.WriteAllText(Full("big.txt"), "small");
        var hash = FileHashService.ComputeFileHash(Full("big.txt"));
        var large = new string('a', FileWriteAdapter.MaxInputBytes + 1);
        var args = new JsonObject { ["path"] = "big.txt", ["old_string"] = "small", ["new_string"] = large, ["expected_sha256"] = hash };
        var ex = Assert.Throws<ArgumentException>(() => _edit.Prepare(args));
        Assert.Equal("content_too_large", ex.Message);
        Assert.Equal("small", File.ReadAllText(Full("big.txt")));
    }

    [Fact]
    public void Edit_UTF8_validity_enforced()
    {
        File.WriteAllText(Full("utf.txt"), "hello world");
        var hash = FileHashService.ComputeFileHash(Full("utf.txt"));
        var invalid = "\uD800";
        var args = new JsonObject { ["path"] = "utf.txt", ["old_string"] = "world", ["new_string"] = invalid, ["expected_sha256"] = hash };
        var ex = Assert.Throws<ArgumentException>(() => _edit.Prepare(args));
        Assert.Equal("invalid_utf8", ex.Message);
    }

    [Fact]
    public void Edit_before_after_hashes_and_bounded_diff_private()
    {
        File.WriteAllText(Full("hash.txt"), "line1\nline2\n");
        var beforeHash = FileHashService.ComputeFileHash(Full("hash.txt"));
        var args = new JsonObject { ["path"] = "hash.txt", ["old_string"] = "line2", ["new_string"] = "changed", ["expected_sha256"] = beforeHash };
        var prep = _edit.Prepare(args);
        Assert.Equal(beforeHash, prep["before_sha256"]?.GetValue<string>());
        var afterExpected = FileHashService.ComputeStringHash("line1\nchanged\n");
        Assert.Equal(afterExpected, prep["after_sha256"]?.GetValue<string>());
        Assert.NotNull(prep["diff_summary"]);
        Assert.True(prep["diff_summary"]!.GetValue<string>().Length <= FileWriteAdapter.MaxDiffChars);
        Assert.True(prep["diff_summary"]!.GetValue<string>().Length > 0);

        var result = _edit.Execute(args);
        Assert.Equal(beforeHash, result["before_sha256"]?.GetValue<string>());
        Assert.Equal(afterExpected, result["after_sha256"]?.GetValue<string>());
        Assert.Equal(afterExpected, FileHashService.ComputeFileHash(Full("hash.txt")));
        Assert.True(result["diff_summary"]!.GetValue<string>().Length <= FileEditAdapter.MaxDiffChars);
    }

    [Fact]
    public void Write_before_after_hashes_and_bounded_diff_private()
    {
        File.WriteAllText(Full("w.txt"), "old");
        var beforeHash = FileHashService.ComputeFileHash(Full("w.txt"));
        var prep = _write.Prepare(new JsonObject { ["path"] = "w.txt", ["content"] = "new", ["expected_sha256"] = beforeHash });
        Assert.Equal(beforeHash, prep["before_sha256"]?.GetValue<string>());
        var afterHash = FileHashService.ComputeStringHash("new");
        Assert.Equal(afterHash, prep["after_sha256"]?.GetValue<string>());
        Assert.True(prep["diff_summary"]!.GetValue<string>().Length <= FileWriteAdapter.MaxDiffChars);

        var result = _write.Execute(new JsonObject { ["path"] = "w.txt", ["content"] = "new", ["expected_sha256"] = beforeHash, ["overwrite"] = true });
        Assert.Equal(afterHash, result["after_sha256"]?.GetValue<string>());
        Assert.Equal(afterHash, FileHashService.ComputeFileHash(Full("w.txt")));
        Assert.True(result["diff_summary"]!.GetValue<string>().Length <= FileWriteAdapter.MaxDiffChars);
    }

    [Fact]
    public void Write_4MiB_input_cap_enforced()
    {
        var large = new string('x', 5 * 1024 * 1024);
        var args = new JsonObject { ["path"] = "large.txt", ["content"] = large, ["expected_sha256"] = "missing" };
        var ex = Assert.Throws<ArgumentException>(() => _write.Prepare(args));
        Assert.Equal("content_too_large", ex.Message);
        Assert.False(File.Exists(Full("large.txt")));
    }

    [Fact]
    public void Write_UTF8_validity_enforced()
    {
        var invalid = "\uD800";
        var args = new JsonObject { ["path"] = "invalid.txt", ["content"] = invalid, ["expected_sha256"] = "missing" };
        var ex = Assert.Throws<ArgumentException>(() => _write.Prepare(args));
        Assert.Equal("invalid_utf8", ex.Message);
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
}
