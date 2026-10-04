using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
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
    public async Task Provider_continuation_preserves_opaque_bytes_and_is_denied_to_client_and_model()
    {
        const string secret = "ru-owned-secret-token";
        var cryptography = new HarnessPayloadCryptography(new FakeHarnessKeyStore());
        var store = new HarnessPayloadStore(cryptography, new KnownSecretRedactor([secret]));
        var bytes = Encoding.UTF8.GetBytes($"{{\"encrypted_content\":\"{secret}\"}}");
        var intent = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(71),
            HarnessPayloadKind.ProviderContinuation,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            bytes.Length,
            bytes);
        var runId = HarnessStoreTestFixture.Run(71);

        var protectedPayload = await store.ProtectAsync(runId, intent, CancellationToken.None);
        var roundTrip = await cryptography.DecryptAsync(runId, protectedPayload.Intent, protectedPayload.Encrypted, CancellationToken.None);

        Assert.Equal(bytes, roundTrip.ToArray());
        Assert.False(HarnessPayloadStore.CanReadForClient(protectedPayload.Intent));
        Assert.False(HarnessPayloadStore.CanReadForModel(protectedPayload.Intent));
        Assert.Contains(secret, Encoding.UTF8.GetString(roundTrip.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_continuation_reader_binds_run_step_provider_protocol_and_model()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(72);
        var stepValue = "step_" + new string('a', 32);
        var stepId = new ModelStepId(stepValue);
        var bytes = Encoding.UTF8.GetBytes($"{{\"model_step_id\":\"{stepValue}\",\"Provider\":\"openai\",\"Protocol\":\"openai_responses\",\"Model\":\"gpt-5.6-luna\",\"Version\":1,\"output_items\":[{{\"type\":\"function_call\",\"id\":\"item-72\",\"call_id\":\"call-72\",\"name\":\"files.read\",\"arguments\":\"{{}}\"}}]}}");
        var continuation = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(72), HarnessPayloadKind.ProviderContinuation,
            HarnessPayloadAudience.InternalOnly, HarnessPayloadEncoding.Opaque,
            ProviderContinuation.MaximumBytes + 2048, bytes);
        var request = fixture.NewRun(72, 72, 72, payloads: [continuation]);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);

        var content = await fixture.PayloadStore.ReadProviderContinuationAsync(
            request.Run.RunId, continuation.PayloadId, stepId, "openai", "openai_responses", "gpt-5.6-luna", CancellationToken.None);
        Assert.Equal(bytes, content.Plaintext.ToArray());
        var wrongStep = await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadProviderContinuationAsync(
            request.Run.RunId, continuation.PayloadId, new ModelStepId("step_" + new string('b', 32)), "openai", "openai_responses", "gpt-5.6-luna", CancellationToken.None));
        var wrongModel = await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadProviderContinuationAsync(
            request.Run.RunId, continuation.PayloadId, stepId, "openai", "openai_responses", "other-model", CancellationToken.None));
        Assert.Equal("provider_continuation_invalid", wrongStep.Code);
        Assert.Equal("provider_continuation_invalid", wrongModel.Code);
        await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadForClientAsync(
            request.Run.RunId, continuation.PayloadId, CancellationToken.None));
        await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadForModelAsync(
            request.Run.RunId, continuation.PayloadId, CancellationToken.None));
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
    public async Task Prompt_receipt_payload_is_internal_only_and_json_bounded()
    {
        var store = new HarnessPayloadStore(
            new HarnessPayloadCryptography(new FakeHarnessKeyStore()),
            KnownSecretRedactor.Empty);
        var bytes = Encoding.UTF8.GetBytes(
            "{\"composer_version\":\"1.0.0\",\"prompt_sha256\":\"" +
            new string('0', 64) + "\",\"block_digests\":[]}");
        var intent = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(3),
            HarnessPayloadKind.PromptReceipt,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            16 * 1024,
            bytes);

        var protectedPayload = await store.ProtectAsync(
            HarnessStoreTestFixture.Run(3),
            intent,
            CancellationToken.None);

        Assert.Equal(HarnessPayloadKind.PromptReceipt, protectedPayload.Intent.Kind);
        Assert.False(HarnessPayloadStore.CanReadForClient(intent));
        Assert.False(HarnessPayloadStore.CanReadForModel(intent));
        var wrongAudience = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(4),
            HarnessPayloadKind.PromptReceipt,
            HarnessPayloadAudience.AuthenticatedClient
                | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            16 * 1024,
            bytes);
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ProtectAsync(
                HarnessStoreTestFixture.Run(3),
                wrongAudience,
                CancellationToken.None).AsTask());
        Assert.Equal("payload_audience_invalid", error.Code);
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
