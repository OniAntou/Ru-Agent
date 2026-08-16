using System.Security.Cryptography;

namespace RuBackend.Business.Settings;

public sealed record CredentialMigrationResult(bool Success, IReadOnlyList<string> MigratedAccounts);

public sealed class CredentialMigrator
{
    private const string LegacyServiceName = "mini_gpt";
    private const string CanonicalServiceName = "Ru";

    private static readonly string[] Accounts =
        ["api_key", "kilo_gateway_api_key", "gemini_vision_api_key"];

    internal static IReadOnlyList<string> MigratableAccounts => Accounts;

    private readonly ICredentialStore _store;

    public CredentialMigrator(ICredentialStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CredentialMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        var migrated = new List<string>();
        foreach (var account in Accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacy = await _store.GetAsync(LegacyServiceName, account, cancellationToken)
                .ConfigureAwait(false);
            if (legacy is null)
            {
                continue;
            }

            await _store.SetAsync(CanonicalServiceName, account, legacy, cancellationToken)
                .ConfigureAwait(false);
            var canonical = await _store.GetAsync(CanonicalServiceName, account, cancellationToken)
                .ConfigureAwait(false);
            if (canonical is null || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(legacy),
                    System.Text.Encoding.UTF8.GetBytes(canonical)))
            {
                return new CredentialMigrationResult(false, migrated);
            }

            await _store.DeleteAsync(LegacyServiceName, account, cancellationToken)
                .ConfigureAwait(false);
            migrated.Add(account);
        }

        return new CredentialMigrationResult(true, migrated);
    }
}