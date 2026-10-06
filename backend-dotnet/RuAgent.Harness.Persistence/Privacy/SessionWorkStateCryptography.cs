using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Privacy;

public sealed class SessionWorkStateCryptography(IHarnessKeyStore keyStore)
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Version = 1;

    public async ValueTask<EncryptedHarnessPayload> ProtectAsync(
        SessionWorkState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var plaintext = SessionWorkStateCodec.Serialize(state);
        var key = await GetKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(state));
            return new EncryptedHarnessPayload(Version, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async ValueTask<SessionWorkState> UnprotectAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        string workspaceIdentitySha256,
        RunId sourceRunId,
        long version,
        EventId sourceEventId,
        EncryptedHarnessPayload envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ProtectionVersion != Version
            || envelope.Nonce.Length != NonceBytes
            || envelope.AuthenticationTag.Length != TagBytes
            || envelope.Ciphertext.Length is 0 or > SessionWorkState.MaximumUtf8Bytes)
            throw new HarnessStoreException("session_work_state_invalid");
        var key = await GetKeyAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(envelope.Nonce.Span, envelope.Ciphertext.Span,
                envelope.AuthenticationTag.Span, plaintext,
                AssociatedData(sessionId, workspaceId, workspaceIdentitySha256, sourceRunId, version, sourceEventId));
            var state = SessionWorkStateCodec.Deserialize(plaintext);
            if (state.SessionId != sessionId || state.WorkspaceId != workspaceId
                || state.WorkspaceIdentitySha256 != workspaceIdentitySha256
                || state.SourceRunId != sourceRunId || state.Version != version
                || state.SourceEventId != sourceEventId.Value)
                throw new HarnessStoreException("session_work_state_binding_invalid");
            return state;
        }
        catch (CryptographicException error)
        {
            throw new HarnessStoreException("session_work_state_authentication_failed", error);
        }
        catch (InvalidDataException error)
        {
            throw new HarnessStoreException("session_work_state_invalid", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        var key = await keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        if (key is { Length: 32 }) return key;
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        throw new HarnessStoreException("harness_key_invalid");
    }

    private static byte[] AssociatedData(SessionWorkState state) =>
        AssociatedData(state.SessionId, state.WorkspaceId, state.WorkspaceIdentitySha256, state.SourceRunId,
            state.Version, new EventId(state.SourceEventId));

    private static byte[] AssociatedData(
        SessionId sessionId,
        WorkspaceId workspaceId,
        string workspaceIdentitySha256,
        RunId sourceRunId,
        long version,
        EventId sourceEventId) => Encoding.UTF8.GetBytes(
            $"session-work-state|{Version}|{sessionId.Value}|{workspaceId.Value}|{workspaceIdentitySha256}|{sourceRunId.Value}|{version}|{sourceEventId.Value}");
}
