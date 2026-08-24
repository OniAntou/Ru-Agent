using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessPayloadStoreTests
{
    [Fact]
    public async Task Cryptography_zeroes_each_caller_owned_key_after_use()
    {
        var keyStore = new TrackingKeyStore();
        var cryptography = new HarnessPayloadCryptography(keyStore);
        var intent = TextIntent(1, "protected");
        var runId = HarnessStoreTestFixture.Run(1);

        var encrypted = await cryptography.EncryptAsync(
            runId,
            intent,
            CancellationToken.None);
        _ = await cryptography.DecryptAsync(
            runId,
            intent,
            encrypted,
            CancellationToken.None);

        Assert.Equal(2, keyStore.IssuedKeys.Count);
        Assert.All(keyStore.IssuedKeys, key =>
            Assert.All(key, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task Equal_plaintext_uses_distinct_nonces_and_round_trips()
    {
        var cryptography = new HarnessPayloadCryptography(new FakeHarnessKeyStore());
        var intent = TextIntent(1, "same plaintext");
        var runId = HarnessStoreTestFixture.Run(1);

        var first = await cryptography.EncryptAsync(runId, intent, CancellationToken.None);
        var second = await cryptography.EncryptAsync(runId, intent, CancellationToken.None);
        var plaintext = await cryptography.DecryptAsync(
            runId,
            intent,
            first,
            CancellationToken.None);

        Assert.NotEqual(first.Nonce.ToArray(), second.Nonce.ToArray());
        Assert.NotEqual(first.Ciphertext.ToArray(), second.Ciphertext.ToArray());
        Assert.Equal(intent.Plaintext.ToArray(), plaintext.ToArray());
    }

    [Fact]
    public async Task Wrong_run_payload_or_tampering_fails_authentication()
    {
        var cryptography = new HarnessPayloadCryptography(new FakeHarnessKeyStore());
        var intent = TextIntent(1, "protected");
        var runId = HarnessStoreTestFixture.Run(1);
        var encrypted = await cryptography.EncryptAsync(runId, intent, CancellationToken.None);

        var wrongRun = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            cryptography.DecryptAsync(
                HarnessStoreTestFixture.Run(2),
                intent,
                encrypted,
                CancellationToken.None).AsTask());
        var wrongPayloadIntent = TextIntent(2, "protected");
        var wrongPayload = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            cryptography.DecryptAsync(
                runId,
                wrongPayloadIntent,
                encrypted,
                CancellationToken.None).AsTask());
        var modifiedBytes = encrypted.Ciphertext.ToArray();
        modifiedBytes[0] ^= 0x80;
        var tampered = encrypted with { Ciphertext = modifiedBytes };
        var altered = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            cryptography.DecryptAsync(
                runId,
                intent,
                tampered,
                CancellationToken.None).AsTask());

        Assert.Equal("payload_authentication_failed", wrongRun.Code);
        Assert.Equal("payload_authentication_failed", wrongPayload.Code);
        Assert.Equal("payload_authentication_failed", altered.Code);
    }

    [Fact]
    public async Task Store_redacts_text_before_encryption_but_preserves_recovery_bytes()
    {
        const string secret = "ru-owned-secret-token";
        var cryptography = new HarnessPayloadCryptography(new FakeHarnessKeyStore());
        var store = new HarnessPayloadStore(
            cryptography,
            new KnownSecretRedactor([secret]));
        var text = TextIntent(1, $"before {secret} after");
        var runId = HarnessStoreTestFixture.Run(1);

        var protectedText = await store.ProtectAsync(runId, text, CancellationToken.None);
        var decryptedText = await cryptography.DecryptAsync(
            runId,
            protectedText.Intent,
            protectedText.Encrypted,
            CancellationToken.None);
        var recoveryBytes = RandomNumberGenerator.GetBytes(128);
        var recovery = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(2),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            recoveryBytes.Length,
            recoveryBytes);
        var protectedRecovery = await store.ProtectAsync(
            runId,
            recovery,
            CancellationToken.None);
        var decryptedRecovery = await cryptography.DecryptAsync(
            runId,
            protectedRecovery.Intent,
            protectedRecovery.Encrypted,
            CancellationToken.None);

        var textValue = Encoding.UTF8.GetString(decryptedText.Span);
        Assert.DoesNotContain(secret, textValue, StringComparison.Ordinal);
        Assert.Contains(KnownSecretRedactor.Replacement, textValue, StringComparison.Ordinal);
        Assert.Equal(recoveryBytes, decryptedRecovery.ToArray());
        Assert.False(HarnessPayloadStore.CanReadForClient(recovery));
        Assert.False(HarnessPayloadStore.CanReadForModel(recovery));
    }

    [Fact]
    public async Task Per_kind_policy_rejects_oversized_or_wrong_audience_before_encryption()
    {
        var store = new HarnessPayloadStore(
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        var oversizedBytes = new byte[ModelToolCall.MaximumArgumentBytes + 1];
        var oversized = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(1),
            HarnessPayloadKind.ToolArguments,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            oversizedBytes.Length,
            oversizedBytes);
        var wrongAudience = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(2),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Utf8Text,
            32,
            Encoding.UTF8.GetBytes("prompt"));

        var tooLarge = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ProtectAsync(
                HarnessStoreTestFixture.Run(1),
                oversized,
                CancellationToken.None).AsTask());
        var audience = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ProtectAsync(
                HarnessStoreTestFixture.Run(1),
                wrongAudience,
                CancellationToken.None).AsTask());

        Assert.Equal("payload_size_exceeded", tooLarge.Code);
        Assert.Equal("payload_audience_invalid", audience.Code);
    }

    [Fact]
    public async Task Sqlite_payloads_are_ciphertext_and_recovery_is_internal_active_run_once_only()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var visible = TextIntent(10, "visible but encrypted");
        var recoveryBytes = RandomNumberGenerator.GetBytes(64);
        var recovery = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(11),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            recoveryBytes.Length,
            recoveryBytes);
        var request = fixture.NewRun(
            1,
            1,
            1,
            payloads: [visible, recovery]);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);

        var client = await fixture.PayloadStore.ReadForClientAsync(
            request.Run.RunId,
            visible.PayloadId,
            CancellationToken.None);
        var model = await fixture.PayloadStore.ReadForModelAsync(
            request.Run.RunId,
            visible.PayloadId,
            CancellationToken.None);
        var clientDenied = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForClientAsync(
                request.Run.RunId,
                recovery.PayloadId,
                CancellationToken.None));
        var recovered = await fixture.PayloadStore.ReadRecoveryOnceAsync(
            request.Run.RunId,
            recovery.PayloadId,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var consumed = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadRecoveryOnceAsync(
                request.Run.RunId,
                recovery.PayloadId,
                HarnessStoreTestFixture.Timestamp.AddMinutes(2),
                CancellationToken.None));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT protected_content, protection_version, length(nonce), length(authentication_tag)
            FROM agent_payloads
            WHERE payload_id = $payload_id
            """;
        command.Parameters.AddWithValue("$payload_id", visible.PayloadId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var ciphertext = (byte[])reader[0];

        Assert.Equal("visible but encrypted", Encoding.UTF8.GetString(client.Plaintext.Span));
        Assert.Equal(client.Plaintext.ToArray(), model.Plaintext.ToArray());
        Assert.Equal("payload_client_forbidden", clientDenied.Code);
        Assert.Equal(recoveryBytes, recovered.Plaintext.ToArray());
        Assert.Equal("payload_recovery_consumed", consumed.Code);
        Assert.DoesNotContain("visible but encrypted", Encoding.UTF8.GetString(ciphertext));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(12, reader.GetInt32(2));
        Assert.Equal(16, reader.GetInt32(3));
    }

    private static HarnessPayloadIntent TextIntent(int number, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(number),
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(bytes.Length, 1),
            bytes);
    }

    private sealed class TrackingKeyStore : IHarnessKeyStore
    {
        public List<byte[]> IssuedKeys { get; } = [];

        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Enumerable.Repeat((byte)0x5a, 32).ToArray();
            IssuedKeys.Add(key);
            return ValueTask.FromResult(key);
        }
    }
}
