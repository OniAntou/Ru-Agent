using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence;

public sealed record EncryptedWorkspaceJournal(
    int ProtectionVersion,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> AuthenticationTag);

public sealed class WorkspaceJournalProtector
{
    public const int CurrentProtectionVersion = 1;
    private readonly IHarnessKeyStore _keyStore;

    public WorkspaceJournalProtector(IHarnessKeyStore keyStore) =>
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));

    public async ValueTask<EncryptedWorkspaceJournal> EncryptAsync(
        WorkspaceId workspaceId,
        string changeId,
        string normalizedPath,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        ValidateMetadata(changeId, normalizedPath);
        var key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, AssociatedData(workspaceId, changeId, normalizedPath));
            return new EncryptedWorkspaceJournal(CurrentProtectionVersion, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> DecryptAsync(
        WorkspaceId workspaceId,
        string changeId,
        string normalizedPath,
        EncryptedWorkspaceJournal encrypted,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        ValidateMetadata(changeId, normalizedPath);
        if (encrypted.ProtectionVersion != CurrentProtectionVersion
            || encrypted.Nonce.Length != 12
            || encrypted.AuthenticationTag.Length != 16)
        {
            throw new HarnessStoreException("journal_envelope_invalid");
        }

        var key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintext = new byte[encrypted.Ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            try
            {
                aes.Decrypt(
                    encrypted.Nonce.Span,
                    encrypted.Ciphertext.Span,
                    encrypted.AuthenticationTag.Span,
                    plaintext,
                    AssociatedData(workspaceId, changeId, normalizedPath));
                return plaintext;
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new HarnessStoreException("journal_authentication_failed", exception);
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

            throw new HarnessStoreException("journal_key_invalid");
        }

        return key;
    }

    private static byte[] AssociatedData(WorkspaceId workspaceId, string changeId, string normalizedPath) =>
        Encoding.UTF8.GetBytes($"v={CurrentProtectionVersion};workspace={workspaceId.Value};change={changeId};path={normalizedPath}");

    private static void ValidateMetadata(string changeId, string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(changeId) || changeId.Length > 256)
        {
            throw new ArgumentException("Change ID is invalid.", nameof(changeId));
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath.Length > 4096 || Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException("Journal path is invalid.", nameof(normalizedPath));
        }
    }
}
