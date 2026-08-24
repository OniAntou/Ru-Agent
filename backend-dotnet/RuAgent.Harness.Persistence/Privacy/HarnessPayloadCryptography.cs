using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence.Privacy;

public sealed record EncryptedHarnessPayload
{
    public EncryptedHarnessPayload(
        int protectionVersion,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> authenticationTag)
    {
        ProtectionVersion = protectionVersion;
        Nonce = nonce.ToArray();
        Ciphertext = ciphertext.ToArray();
        AuthenticationTag = authenticationTag.ToArray();
    }

    public int ProtectionVersion { get; init; }

    public ReadOnlyMemory<byte> Nonce { get; init; }

    public ReadOnlyMemory<byte> Ciphertext { get; init; }

    public ReadOnlyMemory<byte> AuthenticationTag { get; init; }
}

public sealed class HarnessPayloadCryptography
{
    public const int CurrentProtectionVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly IHarnessKeyStore _keyStore;

    public HarnessPayloadCryptography(IHarnessKeyStore keyStore) =>
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));

    public async ValueTask<EncryptedHarnessPayload> EncryptAsync(
        RunId runId,
        HarnessPayloadIntent intent,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Plaintext.Length > intent.ByteCap)
        {
            throw new HarnessStoreException("payload_size_exceeded");
        }

        var key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[intent.Plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                nonce,
                intent.Plaintext.Span,
                ciphertext,
                tag,
                AssociatedData(runId, intent));
            return new EncryptedHarnessPayload(
                CurrentProtectionVersion,
                nonce,
                ciphertext,
                tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> DecryptAsync(
        RunId runId,
        HarnessPayloadIntent metadata,
        EncryptedHarnessPayload encrypted,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(encrypted);
        if (encrypted.ProtectionVersion != CurrentProtectionVersion
            || encrypted.Nonce.Length != NonceSize
            || encrypted.AuthenticationTag.Length != TagSize
            || encrypted.Ciphertext.Length > metadata.ByteCap)
        {
            throw new HarnessStoreException("payload_envelope_invalid");
        }

        var key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintext = new byte[encrypted.Ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            try
            {
                aes.Decrypt(
                    encrypted.Nonce.Span,
                    encrypted.Ciphertext.Span,
                    encrypted.AuthenticationTag.Span,
                    plaintext,
                    AssociatedData(runId, metadata));
                return plaintext;
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new HarnessStoreException("payload_authentication_failed", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async ValueTask<byte[]> ReadKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        if (key is not { Length: 32 })
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            throw new HarnessStoreException("harness_key_invalid");
        }

        return key;
    }

    private static byte[] AssociatedData(RunId runId, HarnessPayloadIntent intent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentProtectionVersion);
            writer.WriteString("run_id", runId.Value);
            writer.WriteString("payload_id", intent.PayloadId.Value);
            writer.WriteNumber("kind", (int)intent.Kind);
            writer.WriteNumber("audience", (int)intent.Audience);
            writer.WriteNumber("encoding", (int)intent.Encoding);
            writer.WriteNumber("byte_cap", intent.ByteCap);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
