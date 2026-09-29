using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FilePatchAdapterTests : IDisposable
{
    private readonly string _root;

    public FilePatchAdapterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string BuildPatch(string path, string oldContent, string newContent)
    {
        oldContent ??= string.Empty;
        newContent ??= string.Empty;
        var oldLines = oldContent.Replace("\r\n", "\n").Split('\n');
        var newLines = newContent.Replace("\r\n", "\n").Split('\n');
        if (oldLines.Length == 1 && oldLines[0] == string.Empty) oldLines = Array.Empty<string>();
        if (newLines.Length == 1 && newLines[0] == string.Empty) newLines = Array.Empty<string>();

        var sb = new StringBuilder();
        sb.Append($"--- a/{path}\n");
        sb.Append($"+++ b/{path}\n");
        sb.Append($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@\n");
        int max = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < max; i++)
        {
            if (i < oldLines.Length && i < newLines.Length)
            {
                if (oldLines[i] == newLines[i])
                {
                    sb.Append($" {oldLines[i]}\n");
                }
                else
                {
                    sb.Append($"-{oldLines[i]}\n");
                    sb.Append($"+{newLines[i]}\n");
                }
            }
            else if (i < oldLines.Length)
            {
                sb.Append($"-{oldLines[i]}\n");
            }
            else
            {
                sb.Append($"+{newLines[i]}\n");
            }
        }
        return sb.ToString();
    }

    private static string BuildPatchMulti(params (string path, string oldContent, string newContent)[] files)
    {
        var sb = new StringBuilder();
        foreach (var (path, oldC, newC) in files)
        {
            sb.Append(BuildPatch(path, oldC, newC));
        }
        return sb.ToString();
    }

    private static string Hash(string content) => FileHashService.ComputeStringHash(content);

    [Fact]
    public void Schema_requires_patch_and_expected_hashes_for_every_target()
    {
        var adapter = new FilePatchAdapter(_root);
        var patch = BuildPatch("a.txt", "old", "new");
        var missingHashes = new JsonObject { ["patch"] = patch };
        Assert.Throws<ArgumentException>(() => adapter.Prepare(missingHashes));

        var missingPatch = new JsonObject { ["expected_hashes"] = new JsonObject { ["a.txt"] = new string('a', 64) } };
        Assert.Throws<ArgumentException>(() => adapter.Prepare(missingPatch));

        File.WriteAllText(Full("a.txt"), "old");
        var hash = FileHashService.ComputeFileHash(Full("a.txt"));
        var incompleteHashes = new JsonObject { ["patch"] = patch, ["expected_hashes"] = new JsonObject() };
        Assert.Throws<ArgumentException>(() => adapter.Prepare(incompleteHashes));

        var wrongCount = new JsonObject { ["patch"] = patch + BuildPatch("b.txt", "", "new2"), ["expected_hashes"] = new JsonObject { ["a.txt"] = hash } };
        File.WriteAllText(Full("b.txt"), "");
        Assert.Throws<ArgumentException>(() => adapter.Prepare(wrongCount));
    }

    [Fact]
    public void Schema_requires_lowercase_64hex_or_missing_per_target()
    {
        var adapter = new FilePatchAdapter(_root);
        var patch = BuildPatch("a.txt", "old", "new");
        File.WriteAllText(Full("a.txt"), "old");
        var upper = new JsonObject { ["patch"] = patch, ["expected_hashes"] = new JsonObject { ["a.txt"] = new string('A', 64) } };
        Assert.Throws<ArgumentException>(() => adapter.Prepare(upper));

        var shortHash = new JsonObject { ["patch"] = patch, ["expected_hashes"] = new JsonObject { ["a.txt"] = "abc" } };
        Assert.Throws<ArgumentException>(() => adapter.Prepare(shortHash));
    }

    [Fact]
    public void Validate_all_targets_before_first_replacement_failure_on_target_N_writes_none()
    {
        File.WriteAllText(Full("a.txt"), "contentA");
        File.WriteAllText(Full("b.txt"), "contentB");
        var hashA = FileHashService.ComputeFileHash(Full("a.txt"));
        var hashB = FileHashService.ComputeFileHash(Full("b.txt"));
        var wrongB = new string('a', 64);

        var patch = BuildPatchMulti(
            ("a.txt", "contentA", "newA"),
            ("b.txt", "contentB", "newB")
        );

        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hashA, ["b.txt"] = wrongB }
        };

        var ex = Assert.Throws<UnauthorizedAccessException>(() => adapter.Execute(args));
        Assert.Equal("workspace_changed", ex.Message);
        Assert.Equal("contentA", File.ReadAllText(Full("a.txt")));
        Assert.Equal("contentB", File.ReadAllText(Full("b.txt")));
    }

    [Fact]
    public void Successful_multi_file_patch_is_atomic_and_correct()
    {
        File.WriteAllText(Full("a.txt"), "hello");
        File.WriteAllText(Full("b.txt"), "world");
        var hashA = FileHashService.ComputeFileHash(Full("a.txt"));
        var hashB = FileHashService.ComputeFileHash(Full("b.txt"));

        var patch = BuildPatchMulti(
            ("a.txt", "hello", "hi"),
            ("b.txt", "world", "earth")
        );

        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hashA, ["b.txt"] = hashB }
        };

        var prep = adapter.Prepare(args);
        Assert.Equal(2, prep["count"]?.GetValue<int>());

        var result = adapter.Execute(args);
        Assert.Equal(2, result["count"]?.GetValue<int>());
        Assert.Equal("hi", File.ReadAllText(Full("a.txt")));
        Assert.Equal("earth", File.ReadAllText(Full("b.txt")));

        var files = result["files"]!.AsArray();
        var aEntry = files.First(n => n!["path"]!.GetValue<string>() == "a.txt");
        Assert.Equal(hashA, aEntry!["before_sha256"]!.GetValue<string>());
        Assert.Equal(Hash("hi"), aEntry!["after_sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Journal_restores_prior_replacements_on_simulated_failure()
    {
        File.WriteAllText(Full("a.txt"), "origA");
        File.WriteAllText(Full("b.txt"), "origB");
        var hashA = FileHashService.ComputeFileHash(Full("a.txt"));
        var hashB = FileHashService.ComputeFileHash(Full("b.txt"));

        var patch = BuildPatchMulti(
            ("a.txt", "origA", "newA"),
            ("b.txt", "origB", "newB")
        );

        var adapter = new FilePatchAdapter(_root, simulateFailure: path => path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hashA, ["b.txt"] = hashB }
        };

        var ex = Assert.Throws<IOException>(() => adapter.Execute(args));
        Assert.Equal("simulated_replacement_failure", ex.Message);
        Assert.Equal("origA", File.ReadAllText(Full("a.txt")));
        Assert.Equal("origB", File.ReadAllText(Full("b.txt")));
    }

    [Fact]
    public void No_path_can_repeat()
    {
        var patch = BuildPatch("a.txt", "old", "new") + BuildPatch("a.txt", "old2", "new2");
        File.WriteAllText(Full("a.txt"), "old");
        var hash = FileHashService.ComputeFileHash(Full("a.txt"));
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hash }
        };
        var ex = Assert.Throws<ArgumentException>(() => adapter.Prepare(args));
        Assert.Equal("patch_duplicate_path", ex.Message);
        Assert.Equal("old", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void No_path_can_escape_outside_or_protected()
    {
        File.WriteAllText(Full("a.txt"), "old");
        var hash = FileHashService.ComputeFileHash(Full("a.txt"));

        var escapePatch = BuildPatch("../outside.txt", "old", "new");
        var adapter = new FilePatchAdapter(_root);
        var argsEscape = new JsonObject
        {
            ["patch"] = escapePatch,
            ["expected_hashes"] = new JsonObject { ["../outside.txt"] = hash }
        };
        var ex1 = Assert.ThrowsAny<Exception>(() => adapter.Prepare(argsEscape));
        Assert.True(ex1 is UnauthorizedAccessException || ex1 is ArgumentException);

        var protectedPatch = BuildPatch(".env.local", "old", "new");
        var argsProtected = new JsonObject
        {
            ["patch"] = protectedPatch,
            ["expected_hashes"] = new JsonObject { [".env.local"] = hash }
        };
        var ex2 = Assert.ThrowsAny<Exception>(() => adapter.Prepare(argsProtected));
        Assert.True(ex2 is UnauthorizedAccessException || ex2 is ArgumentException);

        var absPatch = BuildPatch("/tmp/absolute.txt", "old", "new");
        var argsAbs = new JsonObject
        {
            ["patch"] = absPatch,
            ["expected_hashes"] = new JsonObject { ["/tmp/absolute.txt"] = hash }
        };
        var ex3 = Assert.ThrowsAny<Exception>(() => adapter.Prepare(argsAbs));
        Assert.True(ex3 is UnauthorizedAccessException || ex3 is ArgumentException);
    }

    [Fact]
    public void Create_new_file_via_patch_with_missing_expected()
    {
        var patch = BuildPatch("new.txt", "", "created content");
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["new.txt"] = "missing" }
        };
        var result = adapter.Execute(args);
        Assert.Equal(1, result["count"]?.GetValue<int>());
        Assert.Equal("created content", File.ReadAllText(Full("new.txt")));
        var entry = result["files"]![0]!;
        Assert.Equal("missing", entry["before_sha256"]!.GetValue<string>());
        Assert.Equal(Hash("created content"), entry["after_sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Link_replacement_between_prepare_and_execute_is_detected_for_patch()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-patch-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
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
            var patch = BuildPatch("link/file.txt", "inside", "new inside");
            var adapter = new FilePatchAdapter(_root);
            var args = new JsonObject
            {
                ["patch"] = patch,
                ["expected_hashes"] = new JsonObject { ["link/file.txt"] = hash }
            };
            var prep = adapter.Prepare(args);
            Assert.NotNull(prep);

            Directory.Delete(linkDir, false);
            bool recreated = TryCreateJunction(linkDir, outside);
            if (!recreated) return;
            File.WriteAllText(Path.Combine(outside, "file.txt"), "evil");

            var ex = Assert.Throws<UnauthorizedAccessException>(() => adapter.Execute(args));
            Assert.Equal("workspace_changed", ex.Message);
            Assert.Equal("inside", File.ReadAllText(Path.Combine(realDir, "file.txt")));
        }
        finally
        {
            try { if (Directory.Exists(linkDir)) Directory.Delete(linkDir, false); } catch { }
            Directory.Delete(outside, true);
            try { Directory.Delete(realDir, true); } catch { }
        }
    }

    [Fact]
    public void Patch_rejects_hard_link_before_diff_or_mutation()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-patch-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-patch-marker";
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
            var adapter = new FilePatchAdapter(_root);
            var arguments = new JsonObject
            {
                ["patch"] = BuildPatch("leak.txt", content, "replacement"),
                ["expected_hashes"] = new JsonObject { ["leak.txt"] = Hash(content) },
            };
            var prepareError = Assert.Throws<UnauthorizedAccessException>(() => adapter.Prepare(arguments));
            Assert.Equal("hard_link_not_allowed", prepareError.Message);
            var executeError = Assert.Throws<UnauthorizedAccessException>(() => adapter.Execute(arguments));
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
    public void Patch_bounded_and_rejected_if_too_large()
    {
        var largePatch = new string('a', FilePatchAdapter.MaxPatchBytes + 1);
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = largePatch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = new string('a', 64) }
        };
        var ex = Assert.Throws<ArgumentException>(() => adapter.Prepare(args));
        Assert.Equal("patch_too_large", ex.Message);
    }

    [Fact]
    public void Patch_with_mismatched_context_is_rejected_before_mutation()
    {
        File.WriteAllText(Full("a.txt"), "hello world");
        var hash = FileHashService.ComputeFileHash(Full("a.txt"));
        var patch = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-goodbye world\n+hello there\n";
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hash }
        };

        var ex = Assert.Throws<ArgumentException>(() => adapter.Prepare(args));
        Assert.Equal("patch_context_mismatch", ex.Message);
        Assert.Equal("hello world", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void Patch_with_fewer_old_lines_than_the_hunk_header_declares_is_rejected()
    {
        File.WriteAllText(Full("a.txt"), "only one line");
        var hash = FileHashService.ComputeFileHash(Full("a.txt"));
        // The header claims two old-side lines; the hunk carries one context line.
        var patch = "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n only one line\n+added\n";
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["a.txt"] = hash }
        };

        var ex = Assert.Throws<ArgumentException>(() => adapter.Prepare(args));
        Assert.Equal("patch_context_mismatch", ex.Message);
    }

    [Fact]
    public void Dev_null_source_header_parses_as_a_new_file_patch()
    {
        var patch = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,2 @@\n+first\n+second\n";
        var adapter = new FilePatchAdapter(_root);
        var args = new JsonObject
        {
            ["patch"] = patch,
            ["expected_hashes"] = new JsonObject { ["new.txt"] = "missing" }
        };

        var prepared = adapter.Prepare(args);
        Assert.Equal(1, prepared["count"]!.GetValue<int>());

        var result = adapter.Execute(args);
        Assert.Equal(1, result["count"]!.GetValue<int>());
        Assert.Equal("first\nsecond", File.ReadAllText(Full("new.txt")));
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
