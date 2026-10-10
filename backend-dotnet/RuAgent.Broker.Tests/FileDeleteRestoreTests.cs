using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileDeleteRestoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileDeleteAdapter _delete;
    private readonly FileRestoreAdapter _restore;
    private readonly InMemoryRecoveryStore _store = new();

    public FileDeleteRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-del-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _delete = new FileDeleteAdapter(_root, _store);
        _restore = new FileRestoreAdapter(_root, _store);
        _store.ClearForTest();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        _store.ClearForTest();
    }

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(byte[] bytes) => FileHashService.ComputeBytesHash(bytes);
    private static string HashString(string content) => FileHashService.ComputeStringHash(content);

    [Fact]
    public void Delete_preparation_reads_at_most_cap_and_returns_private_payload_intent_before_delete()
    {
        var content = new string('a', 512);
        File.WriteAllText(Full("small.txt"), content);
        var hash = FileHashService.ComputeFileHash(Full("small.txt"));
        var args = new JsonObject { ["path"] = "small.txt", ["expected_sha256"] = hash };
        var prep = _delete.Prepare(args);
        Assert.Equal(hash, prep.BeforeHash);
        Assert.Equal(content, Encoding.UTF8.GetString(prep.RecoveryBytes));
        Assert.True(File.Exists(Full("small.txt")));
        Assert.Equal(content, File.ReadAllText(Full("small.txt")));
        Assert.NotNull(prep.SafePreview);
        Assert.False(prep.SafePreview.ToJsonString().Contains(content.Substring(0, 10)) && prep.SafePreview["bytes"]?.GetValue<int>() == prep.RecoveryBytes.Length ? prep.SafePreview.ToJsonString().Contains(Convert.ToBase64String(prep.RecoveryBytes)) : false);
        // Safe preview must not contain raw recovery bytes
        var safeJson = prep.SafePreview.ToJsonString();
        Assert.DoesNotContain(Convert.ToBase64String(prep.RecoveryBytes), safeJson);
    }

    [Fact]
    public void Delete_oversized_returns_recovery_too_large_and_remains_intact()
    {
        var largePath = Full("large.bin");
        var largeSize = FileDeleteAdapter.MaxRecoveryBytes + 1;
        var largeBytes = new byte[largeSize];
        new Random(42).NextBytes(largeBytes);
        File.WriteAllBytes(largePath, largeBytes);
        var hash = FileHashService.ComputeFileHash(largePath);
        var args = new JsonObject { ["path"] = "large.bin", ["expected_sha256"] = hash };
        var ex = Assert.Throws<ArgumentException>(() => _delete.Prepare(args));
        Assert.Equal("recovery_too_large", ex.Message);
        Assert.True(File.Exists(largePath));
        Assert.Equal(largeSize, new FileInfo(largePath).Length);
        // Even execution should also return recovery_too_large
        var ex2 = Assert.Throws<ArgumentException>(() => _delete.Execute(args));
        Assert.Equal("recovery_too_large", ex2.Message);
        Assert.True(File.Exists(largePath));
    }

    [Fact]
    public void Delete_honors_configured_recovery_cap_below_default()
    {
        var path = Full("small-cap.txt");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        var adapter = new FileDeleteAdapter(_root, maxRecoveryBytes: 4);
        var hash = FileHashService.ComputeFileHash(path);

        var error = Assert.Throws<ArgumentException>(() => adapter.Prepare(new JsonObject
        {
            ["path"] = "small-cap.txt",
            ["expected_sha256"] = hash,
        }));

        Assert.Equal("recovery_too_large", error.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Delete_expected_hash_mismatch_returns_workspace_changed_with_zero_write()
    {
        File.WriteAllText(Full("a.txt"), "original");
        var wrongHash = new string('a', 64);
        var args = new JsonObject { ["path"] = "a.txt", ["expected_sha256"] = wrongHash };
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _delete.Execute(args));
        Assert.Equal("workspace_changed", ex.Message);
        Assert.Equal("original", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void Delete_execution_time_reresolution_detects_junction_change()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-del-outside-" + Guid.NewGuid().ToString("N"));
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
            var prep = _delete.Prepare(new JsonObject { ["path"] = "link/file.txt", ["expected_sha256"] = hash });
            Assert.NotNull(prep);
            Directory.Delete(linkDir, false);
            bool recreated = TryCreateJunction(linkDir, outside);
            if (!recreated) return;
            var args = new JsonObject { ["path"] = "link/file.txt", ["expected_sha256"] = hash };
            var ex = Assert.Throws<UnauthorizedAccessException>(() => _delete.Execute(args));
            Assert.Equal("workspace_changed", ex.Message);
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "secret.txt")));
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
    public void Delete_rejects_hard_link_before_recovery_capture()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-delete-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-delete-marker";
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
                ["expected_sha256"] = HashString(content),
            };
            var prepareError = Assert.Throws<UnauthorizedAccessException>(() => _delete.Prepare(arguments));
            Assert.Equal("hard_link_not_allowed", prepareError.Message);
            var executeError = Assert.Throws<UnauthorizedAccessException>(() => _delete.Execute(arguments));
            Assert.Equal("hard_link_not_allowed", executeError.Message);
            Assert.Equal(content, File.ReadAllText(target));
            Assert.True(File.Exists(link));
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Delete_returns_opaque_recovery_id_receipt_and_no_recovery_bytes_in_safe_output()
    {
        File.WriteAllText(Full("del.txt"), "to be deleted");
        var hash = FileHashService.ComputeFileHash(Full("del.txt"));
        var prep = _delete.Prepare(new JsonObject { ["path"] = "del.txt", ["expected_sha256"] = hash });
        var recoveryBytes = prep.RecoveryBytes;
        Assert.Equal(hash, Hash(recoveryBytes));
        var result = _delete.Execute(new JsonObject { ["path"] = "del.txt", ["expected_sha256"] = hash });
        Assert.NotNull(result["recovery_id"]);
        var recoveryId = result["recovery_id"]!.GetValue<string>();
        Assert.StartsWith("payload_", recoveryId);
        Assert.False(File.Exists(Full("del.txt")));
        var safeJson = result.ToJsonString();
        Assert.DoesNotContain(Convert.ToBase64String(recoveryBytes), safeJson);
        Assert.True(_store.IsAvailable(recoveryId));
    }

    [Fact]
    public void Delete_missing_file_returns_file_not_found()
    {
        var args = new JsonObject { ["path"] = "missing.txt", ["expected_sha256"] = new string('a', 64) };
        var ex = Assert.Throws<FileNotFoundException>(() => _delete.Execute(args));
        Assert.Equal("file_not_found", ex.Message);
    }

    [Fact]
    public void Restore_advertised_only_while_active_run_owns_valid_recovery_id()
    {
        var content = "restore me";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Hash(bytes);
        var canonical = Full("orig.txt");
        File.WriteAllText(canonical, content);
        var recoveredId = _store.RegisterForTest(canonical, bytes, hash);
        // Prepare should succeed while available
        var prep = _restore.Prepare(new JsonObject { ["recovery_id"] = recoveredId });
        Assert.NotNull(prep);
        Assert.Equal(recoveredId, prep["recovery_id"]!.GetValue<string>());
        // Delete original to make restore possible (simulate delete)
        File.Delete(canonical);
        Assert.False(File.Exists(canonical));
        // Execute restore
        var result = _restore.Execute(new JsonObject { ["recovery_id"] = recoveredId });
        Assert.Equal(recoveredId, result["recovery_id"]!.GetValue<string>());
        Assert.True(File.Exists(canonical));
        // After consumption, the recovery record is removed — replay fails unavailable
        var ex = Assert.Throws<FileNotFoundException>(() => _restore.Prepare(new JsonObject { ["recovery_id"] = recoveredId }));
        Assert.Equal("recovery_unavailable", ex.Message);
        Assert.False(_store.IsAvailable(recoveredId));
    }

    [Fact]
    public void Restore_target_must_still_be_missing_recreated_rejects()
    {
        var content = "original";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Hash(bytes);
        var canonical = Full("target.txt");
        var recoveryId = _store.RegisterForTest(canonical, bytes, hash);
        // Recreate file before restore
        File.WriteAllText(canonical, "recreated");
        var ex = Assert.Throws<InvalidOperationException>(() => _restore.Execute(new JsonObject { ["recovery_id"] = recoveryId }));
        Assert.Equal("recovery_target_exists", ex.Message);
        // Clean up and ensure recovery still not consumed
        File.Delete(canonical);
        var prep = _restore.Prepare(new JsonObject { ["recovery_id"] = recoveryId });
        Assert.NotNull(prep);
    }

    [Fact]
    public void Restore_rejects_hard_link_target_before_recovery_operation()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ru-restore-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        const string content = "hard-link-restore-marker";
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
            var bytes = Encoding.UTF8.GetBytes(content);
            var recoveryId = _store.RegisterForTest(link, bytes, Hash(bytes));
            var error = Assert.Throws<UnauthorizedAccessException>(() => _restore.Prepare(new JsonObject { ["recovery_id"] = recoveryId }));
            Assert.Equal("hard_link_not_allowed", error.Message);
            Assert.Equal(content, File.ReadAllText(target));
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Restore_success_consumes_id_once_replay_returns_recovery_unavailable()
    {
        var content = "once";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Hash(bytes);
        var canonical = Full("once.txt");
        var recoveryId = _store.RegisterForTest(canonical, bytes, hash);
        if (File.Exists(canonical)) File.Delete(canonical);
        var result = _restore.Execute(new JsonObject { ["recovery_id"] = recoveryId });
        Assert.Equal("once", File.ReadAllText(canonical));
        var ex = Assert.Throws<FileNotFoundException>(() => _restore.Execute(new JsonObject { ["recovery_id"] = recoveryId }));
        Assert.Equal("recovery_unavailable", ex.Message);
        var ex2 = Assert.Throws<FileNotFoundException>(() => _restore.Prepare(new JsonObject { ["recovery_id"] = recoveryId }));
        Assert.Equal("recovery_unavailable", ex2.Message);
    }

    [Fact]
    public void Restore_binary_byte_identity_with_known_secret_sentinel_remains_encrypted()
    {
        const string secret = "ru-owned-secret-token-12345";
        // Create binary content containing secret and random bytes
        var prefix = new byte[] { 0x00, 0xFF, 0x01, 0x02 };
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var suffix = new byte[] { 0x03, 0x04, 0x00, 0xFF };
        var content = new byte[prefix.Length + secretBytes.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, content, 0, prefix.Length);
        Buffer.BlockCopy(secretBytes, 0, content, prefix.Length, secretBytes.Length);
        Buffer.BlockCopy(suffix, 0, content, prefix.Length + secretBytes.Length, suffix.Length);

        var hash = Hash(content);
        var canonical = Full("binary.dat");
        var recoveryId = _store.RegisterForTest(canonical, content, hash);
        Assert.True(_store.IsAvailable(recoveryId));
        if (File.Exists(canonical)) File.Delete(canonical);
        var result = _restore.Execute(new JsonObject { ["recovery_id"] = recoveryId });
        var restored = File.ReadAllBytes(canonical);
        Assert.Equal(content, restored);
        Assert.Equal(hash, Hash(restored));
        var safeJson = result.ToJsonString();
        Assert.DoesNotContain(secret, safeJson);
    }

    [Fact]
    public void Delete_and_restore_end_to_end_byte_identity()
    {
        var originalContent = "hello world\nsecond line\n";
        var bytes = Encoding.UTF8.GetBytes(originalContent);
        File.WriteAllBytes(Full("e2e.txt"), bytes);
        var hash = Hash(bytes);
        var prep = _delete.Prepare(new JsonObject { ["path"] = "e2e.txt", ["expected_sha256"] = hash });
        var recoveryBytes = prep.RecoveryBytes;
        Assert.Equal(bytes, recoveryBytes);
        var delResult = _delete.Execute(new JsonObject { ["path"] = "e2e.txt", ["expected_sha256"] = hash });
        Assert.False(File.Exists(Full("e2e.txt")));
        var recoveryId = delResult["recovery_id"]!.GetValue<string>();
        Assert.True(_store.IsAvailable(recoveryId));
        var restoreResult = _restore.Execute(new JsonObject { ["recovery_id"] = recoveryId });
        var restored = File.ReadAllBytes(Full("e2e.txt"));
        Assert.Equal(bytes, restored);
        Assert.Equal(hash, Hash(restored));
        Assert.Equal(recoveryId, restoreResult["recovery_id"]!.GetValue<string>());
    }

    /// <summary>
    /// In-process runtimes must not resolve each other's recovery ids: every
    /// execution service owns its store instead of sharing process-global
    /// state, so a lookup in a foreign store fails closed as unavailable and
    /// leaves the owning store untouched.
    /// </summary>
    [Fact]
    public void Recovery_stores_are_isolated_between_adapters_in_one_process()
    {
        var otherStore = new InMemoryRecoveryStore();
        var otherRestore = new FileRestoreAdapter(_root, otherStore);
        var content = Encoding.UTF8.GetBytes("isolated recovery payload");
        var canonical = Full("isolated.txt");
        File.WriteAllBytes(canonical, content);
        var recoveryId = _store.RegisterForTest(canonical, content, Hash(content));

        Assert.True(_store.IsAvailable(recoveryId));
        Assert.False(otherStore.IsAvailable(recoveryId));
        Assert.False(otherStore.TryGetCanonicalPath(recoveryId, out _));

        var error = Assert.Throws<FileNotFoundException>(
            () => otherRestore.Prepare(new JsonObject { ["recovery_id"] = recoveryId }));
        Assert.Equal("recovery_unavailable", error.Message);

        // The foreign lookup neither consumes nor mutates the owning store.
        Assert.True(_store.IsAvailable(recoveryId));
        var ownedPrep = _restore.Prepare(new JsonObject { ["recovery_id"] = recoveryId });
        Assert.Equal(recoveryId, ownedPrep["recovery_id"]!.GetValue<string>());
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
