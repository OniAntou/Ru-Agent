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
        var ciphertext = (byte[])await cmd.ExecuteScalarAsync()!;
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
}
