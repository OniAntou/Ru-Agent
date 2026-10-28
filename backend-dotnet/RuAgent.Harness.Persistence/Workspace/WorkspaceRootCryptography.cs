using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Workspace;

/// <summary>Encrypts canonical workspace roots with the v1 persisted AES-GCM envelope.</summary>
/// <remarks>
/// The workspace ID is authenticated as associated data. Callers remain responsible for
/// checking live filesystem identity after decrypting a root; this helper performs no I/O.
/// </remarks>
public static class WorkspaceRootCryptography
{
    private const string AssociatedDataPrefix = "ru-workspace-root-v1:";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaximumPlaintextBytes = 1024 * 1024;

    public static WorkspaceRootCiphertext Encrypt(
        WorkspaceId workspaceId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext)
    {
        ValidateKey(key);
        if (plaintext.Length is < 1 or > MaximumPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Workspace root must be bounded and non-empty.");
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(workspaceId));
        return new WorkspaceRootCiphertext(nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(
        WorkspaceId workspaceId,
        ReadOnlySpan<byte> key,
        WorkspaceRootCiphertext envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateKey(key);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(envelope.Nonce.Span, envelope.Ciphertext.Span,
                envelope.AuthenticationTag.Span, plaintext, AssociatedData(workspaceId));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static byte[] AssociatedData(WorkspaceId workspaceId) =>
        Encoding.UTF8.GetBytes(AssociatedDataPrefix + workspaceId.Value);

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("Workspace root key must contain 32 bytes.", nameof(key));
        }
    }
}
