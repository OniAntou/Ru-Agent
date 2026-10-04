using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Recovery;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class FileRecoveryStoreTests
{
    [Fact]
    public async Task Recovery_store_honors_configured_cap_below_default()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);
        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty,
            maxRecoveryBytes: 4);
        var content = Encoding.UTF8.GetBytes("12345");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateRecoveryAsync(
            run.Run.RunId,
            "/tmp/workspace/capped.txt",
            content,
            hash,
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        Assert.Equal("recovery_too_large", error.Message);
    }

    [Fact]
    public async Task Recovery_payload_is_byte_exact_encrypted_and_internal_only()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        await store.EnsureSchemaAsync(CancellationToken.None);

        var secret = "ru-owned-secret-token";
        var redactor = new KnownSecretRedactor([secret]);
        var storeWithRedactor = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            redactor);

        // Use storeWithRedactor's payload store to verify recovery bytes are not redacted
        // Create a recovery with secret inside
        var content = Encoding.UTF8.GetBytes($"prefix {secret} suffix");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var canonical = "/tmp/workspace/file.txt"; // logical canonical path for test

        // Create recovery via store (encrypted)
        var reference = await store.CreateRecoveryAsync(run.Run.RunId, canonical, content, hash, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(canonical, reference.CanonicalPath);
        Assert.Equal(hash, reference.ContentSha256);
        Assert.Equal(run.Run.RunId.Value, reference.RunId.Value);
        Assert.StartsWith("payload_", reference.RecoveryId.Value);

        // Verify that DB stores ciphertext, not plaintext
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT protected_content FROM agent_payloads WHERE payload_id = $id";
        cmd.Parameters.AddWithValue("$id", reference.RecoveryId.Value);
        var rawCiphertext = await cmd.ExecuteScalarAsync(CancellationToken.None);
        Assert.NotNull(rawCiphertext);
        var ciphertext = (byte[])rawCiphertext;
        Assert.NotEqual(content, ciphertext);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(ciphertext));

        // Verify public endpoints forbid recovery
        var clientDenied = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForClientAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_client_forbidden", clientDenied.Code);
        var modelDenied = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForModelAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_model_forbidden", modelDenied.Code);

        // But recovery path returns exact bytes
        var recoveredBytes = await store.ConsumeRecoveryAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None);
        Assert.Equal(content, recoveredBytes);

        // Second consume fails with recovery_consumed
        var consumed = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ConsumeRecoveryAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_recovery_consumed", consumed.Code);
    }

    [Fact]
    public async Task Recovery_is_run_owned_and_one_time()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await fixture.RegisterWorkspaceAsync(2);
        var run1 = fixture.NewRun(1, 1, 1);
        var run2 = fixture.NewRun(2, 1, 2);
        await fixture.CreateEventStore().TryCreateRunAsync(run1, CancellationToken.None);
        await fixture.CreateEventStore().TryCreateRunAsync(run2, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        await store.EnsureSchemaAsync(CancellationToken.None);

        var content = Encoding.UTF8.GetBytes("owner test");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var canonical = "/tmp/workspace/owner.txt";
        var reference = await store.CreateRecoveryAsync(run1.Run.RunId, canonical, content, hash, DateTimeOffset.UtcNow, CancellationToken.None);

        // Wrong run cannot consume
        var wrongRun = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ConsumeRecoveryAsync(run2.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_recovery_unavailable", wrongRun.Code);

        // Correct run can consume once
        var bytes = await store.ConsumeRecoveryAsync(run1.Run.RunId, reference.RecoveryId, CancellationToken.None);
        Assert.Equal(content, bytes);

        // Replay
        var replay = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ConsumeRecoveryAsync(run1.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_recovery_consumed", replay.Code);
    }

    [Fact]
    public async Task Recovery_respects_retention_terminal_run_expires_unconsumed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        await store.EnsureSchemaAsync(CancellationToken.None);

        var content = Encoding.UTF8.GetBytes("to expire");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var reference = await store.CreateRecoveryAsync(run.Run.RunId, "/tmp/workspace/expire.txt", content, hash, DateTimeOffset.UtcNow, CancellationToken.None);

        // Simulate terminal run by marking it completed
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE agent_runs SET terminal_at_utc = $term, state = $state WHERE run_id = $run_id";
        update.Parameters.AddWithValue("$term", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        update.Parameters.AddWithValue("$run_id", run.Run.RunId.Value);
        await update.ExecuteNonQueryAsync();

        // After terminal, recovery should be unavailable (either via active run check or explicit expire)
        var expired = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ConsumeRecoveryAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_recovery_unavailable", expired.Code);

        // Explicit expire should delete unconsumed
        await store.ExpireUnconsumedForRunAsync(run.Run.RunId, CancellationToken.None);
        var list = await store.ListActiveAsync(run.Run.RunId, CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task Recovery_expiry_keeps_mapping_and_payload_when_payload_delete_fails()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        var content = Encoding.UTF8.GetBytes("retained after failed cleanup");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var reference = await store.CreateRecoveryAsync(
            run.Run.RunId,
            "/tmp/workspace/atomic-expiry.txt",
            content,
            hash,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var terminal = connection.CreateCommand();
            terminal.CommandText = "UPDATE agent_runs SET terminal_at_utc = $terminal, state = $state WHERE run_id = $run_id";
            terminal.Parameters.AddWithValue("$terminal", DateTimeOffset.UtcNow.ToString("O"));
            terminal.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
            terminal.Parameters.AddWithValue("$run_id", run.Run.RunId.Value);
            await terminal.ExecuteNonQueryAsync();

            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_recovery_payload_delete
                BEFORE DELETE ON agent_payloads
                WHEN OLD.payload_id = '$payload_id'
                BEGIN
                    SELECT RAISE(ABORT, 'forced recovery payload delete failure');
                END;
                """.Replace("$payload_id", reference.RecoveryId.Value, StringComparison.Ordinal);
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.ExpireUnconsumedForRunAsync(run.Run.RunId, CancellationToken.None));

        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_file_recovery WHERE recovery_id = '" + reference.RecoveryId.Value + "' AND consumed_at_utc IS NULL"));
        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_payloads WHERE payload_id = '" + reference.RecoveryId.Value + "' AND consumed_at_utc IS NULL"));
    }

    [Fact]
    public async Task Recovery_binary_byte_identity_preserves_exact_bytes_despite_secret_redaction()
    {
        const string secret = "known-secret-sentinel-42";
        var redactor = new KnownSecretRedactor([secret]);
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        // Direct payload store test: recovery bytes containing secret must remain exact, while same secret in normal payload is redacted
        var cryptography = new HarnessPayloadCryptography(new FakeHarnessKeyStore());
        var payloadStore = new HarnessPayloadStore(fixture.Options, cryptography, redactor);

        var binaryPrefix = new byte[] { 0x00, 0xFF, 0x01 };
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var binarySuffix = new byte[] { 0x02, 0x03, 0x00 };
        var recoveryBytes = new byte[binaryPrefix.Length + secretBytes.Length + binarySuffix.Length];
        Buffer.BlockCopy(binaryPrefix, 0, recoveryBytes, 0, binaryPrefix.Length);
        Buffer.BlockCopy(secretBytes, 0, recoveryBytes, binaryPrefix.Length, secretBytes.Length);
        Buffer.BlockCopy(binarySuffix, 0, recoveryBytes, binaryPrefix.Length + secretBytes.Length, binarySuffix.Length);

        var recoveryHash = Convert.ToHexString(SHA256.HashData(recoveryBytes)).ToLowerInvariant();
        var store = new FileRecoveryStore(fixture.Options, new HarnessPayloadCryptography(new FakeHarnessKeyStore()), redactor);
        await store.EnsureSchemaAsync(CancellationToken.None);
        var reference = await store.CreateRecoveryAsync(run.Run.RunId, "/tmp/workspace/binary.dat", recoveryBytes, recoveryHash, DateTimeOffset.UtcNow, CancellationToken.None);

        // Recovery remains byte-exact
        var recovered = await store.ConsumeRecoveryAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None);
        Assert.Equal(recoveryBytes, recovered);

        // But normal tool output containing same secret is redacted
        var toolContent = Encoding.UTF8.GetBytes($"output {secret} end");
        var toolIntent = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(99),
            HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            toolContent.Length,
            toolContent);
        var protectedTool = await payloadStore.ProtectAsync(run.Run.RunId, toolIntent, CancellationToken.None);
        var decryptedTool = await cryptography.DecryptAsync(run.Run.RunId, protectedTool.Intent, protectedTool.Encrypted, CancellationToken.None);
        var toolText = Encoding.UTF8.GetString(decryptedTool.Span);
        Assert.DoesNotContain(secret, toolText);
        Assert.Contains(KnownSecretRedactor.Replacement, toolText);
    }

    [Fact]
    public async Task Recovery_payload_never_returned_by_public_endpoint_even_after_creation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        await store.EnsureSchemaAsync(CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes("never public");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reference = await store.CreateRecoveryAsync(run.Run.RunId, "/tmp/workspace/secret.txt", bytes, hash, DateTimeOffset.UtcNow, CancellationToken.None);

        // Verify payload endpoint would not return it (via CanReadForClient)
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, audience, encoding FROM agent_payloads WHERE payload_id = $id";
        cmd.Parameters.AddWithValue("$id", reference.RecoveryId.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var kind = (HarnessPayloadKind)reader.GetInt32(0);
        var audience = (HarnessPayloadAudience)reader.GetInt32(1);
        var encoding = (HarnessPayloadEncoding)reader.GetInt32(2);
        Assert.Equal(HarnessPayloadKind.RecoveryOpaque, kind);
        Assert.Equal(HarnessPayloadAudience.InternalOnly, audience);
        Assert.Equal(HarnessPayloadEncoding.Opaque, encoding);
        Assert.False(HarnessPayloadStore.CanReadForClient(new HarnessPayloadIntent(reference.RecoveryId, kind, audience, encoding, 1, Array.Empty<byte>())));
        Assert.False(HarnessPayloadStore.CanReadForModel(new HarnessPayloadIntent(reference.RecoveryId, kind, audience, encoding, 1, Array.Empty<byte>())));
    }

    [Fact]
    public async Task Recovery_markers_roll_back_together_when_decryption_fails()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(run, CancellationToken.None);

        var store = new FileRecoveryStore(
            fixture.Options,
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        var content = Encoding.UTF8.GetBytes("marker rollback");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var reference = await store.CreateRecoveryAsync(
            run.Run.RunId,
            "/tmp/workspace/rollback.txt",
            content,
            hash,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE agent_payloads SET protected_content = $bytes WHERE payload_id = $id";
            corrupt.Parameters.AddWithValue("$bytes", new byte[] { 1, 2, 3 });
            corrupt.Parameters.AddWithValue("$id", reference.RecoveryId.Value);
            await corrupt.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ConsumeRecoveryAsync(run.Run.RunId, reference.RecoveryId, CancellationToken.None));
        Assert.Equal("payload_authentication_failed", error.Code);

        // The failed consume must not leave either marker behind: the payload
        // bytes were never handed back, so the recovery has to stay restorable
        // (here it stays listed as active) and the payload stays unconsumed.
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_file_recovery WHERE consumed_at_utc IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_payloads WHERE consumed_at_utc IS NOT NULL"));
        Assert.Single(await store.ListActiveAsync(run.Run.RunId, CancellationToken.None));
    }

    private static async Task<long> ScalarAsync(HarnessStoreTestFixture fixture, string sql)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
