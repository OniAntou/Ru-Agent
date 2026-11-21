using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuBackend.Business.Settings;

public sealed record LegacyCredentialMigrationResult(
    bool Succeeded,
    bool AlreadyCompleted,
    IReadOnlyList<string> MigratedAccounts);

/// <summary>
/// Moves provider keys left by older Ru builds into the account names the current
/// build reads. The Python build stored its keys in the <c>mini_gpt</c> Credential
/// Manager service, and the first .NET builds used the old <c>api_key</c> /
/// <c>kilo_gateway_api_key</c> accounts under <c>Ru</c>; later provider work renamed
/// those accounts, so a user who updates sees no key and is asked to paste it again.
/// The keys were never lost — only unread — and this migration copies each one,
/// verifies the copy byte-for-byte, then removes the old entry.
/// </summary>
/// <remarks>
/// The migration is one-shot per data root. It never overwrites a key the current
/// build can already read, so a user who already re-entered a key keeps the newer
/// value. A failed copy or a failed old-entry deletion leaves the marker unwritten
/// and the source untouched, so the next Host start retries. No secret is logged.
/// </remarks>
public sealed class LegacyCredentialMigration
{
    internal const string MarkerFileName = "legacy-credentials-migrated.v1";
    private const string CanonicalService = "Ru";
    private const string LegacyPythonService = "mini_gpt";
    private const string LegacySettingsRelativePath = ".minigpt/settings.json";
    private const int MaximumSecretCharacters = WindowsCredentialStore.MaximumSecretCharacters;

    internal sealed record LegacySource(string Service, string Account, string TargetAccount);

    /// <summary>
    /// Sources in preference order. Newer files win: a key written by the last .NET
    /// build (<c>Ru</c>) is preferred over the Python copy (<c>mini_gpt</c>).
    /// </summary>
    internal static IReadOnlyList<LegacySource> Sources { get; } =
    [
        new(CanonicalService, "api_key", "opencode_api_key"),
        new(LegacyPythonService, "api_key", "opencode_api_key"),
        new(CanonicalService, "kilo_gateway_api_key", "kilo_api_key"),
        new(LegacyPythonService, "kilo_gateway_api_key", "kilo_api_key"),
        new(LegacyPythonService, "kilo_api_key", "kilo_api_key"),
        new(LegacyPythonService, "gemini_vision_api_key", "gemini_vision_api_key"),
    ];

    private static readonly (string Field, string TargetAccount)[] LegacySettingsFields =
    [
        ("api_key", "opencode_api_key"),
        ("opencode_api_key", "opencode_api_key"),
        ("kilo_gateway_api_key", "kilo_api_key"),
        ("kilo_api_key", "kilo_api_key"),
        ("gemini_vision_api_key", "gemini_vision_api_key"),
    ];

    private readonly ICredentialStore _credentials;
    private readonly string _markerPath;
    private readonly string? _legacySettingsFile;
    private readonly bool _isWindows;
    private readonly TimeProvider _clock;
    private readonly ILogger<LegacyCredentialMigration>? _logger;

    public LegacyCredentialMigration(
        ICredentialStore credentials,
        DataPaths paths,
        ILogger<LegacyCredentialMigration>? logger = null,
        TimeProvider? clock = null,
        string? userProfile = null)
        : this(
            credentials,
            Path.Combine(paths.BusinessDataRoot, MarkerFileName),
            LegacySettingsPath(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            logger,
            clock,
            isWindows: OperatingSystem.IsWindows())
    {
    }

    internal LegacyCredentialMigration(
        ICredentialStore credentials,
        string markerPath,
        string? legacySettingsFile,
        ILogger<LegacyCredentialMigration>? logger = null,
        TimeProvider? clock = null,
        bool isWindows = true)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
        _legacySettingsFile = legacySettingsFile;
        _isWindows = isWindows;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<LegacyCredentialMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows)
        {
            return new LegacyCredentialMigrationResult(true, AlreadyCompleted: true, Array.Empty<string>());
        }

        if (File.Exists(_markerPath))
        {
            return new LegacyCredentialMigrationResult(true, AlreadyCompleted: true, Array.Empty<string>());
        }

        var migrated = new List<string>();
        var succeeded = true;
        var reachedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await MigrateSourceAsync(source, migrated, cancellationToken).ConfigureAwait(false))
                {
                    reachedTargets.Add(source.TargetAccount);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // A vault failure must not keep the Host from starting. Leave the
                // marker unwritten so the next start retries this source.
                succeeded = false;
                _logger?.LogWarning(
                    "Legacy provider key migration failed for account {Account}: {Error}",
                    source.Account,
                    error.GetType().Name);
            }
        }

        // The old Python settings file could still carry a plaintext key from a
        // build older than its Credential Manager move. Import it only where no
        // newer key was found; the file itself is never rewritten.
        try
        {
            var plaintext = ReadLegacySettings();
            if (plaintext is not null)
            {
                foreach (var (field, targetAccount) in LegacySettingsFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reachedTargets.Contains(targetAccount)
                        || !plaintext.TryGetValue(field, out var secret))
                    {
                        continue;
                    }

                    if (await MigrateSecretAsync(targetAccount, secret, migrated, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        reachedTargets.Add(targetAccount);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            succeeded = false;
            _logger?.LogWarning(
                "Legacy settings key import failed: {Error}",
                error.GetType().Name);
        }

        if (succeeded)
        {
            try
            {
                WriteMarker();
            }
            catch (Exception error)
            {
                succeeded = false;
                _logger?.LogWarning(
                    "Legacy provider key migration could not record completion: {Error}",
                    error.GetType().Name);
            }
        }

        return new LegacyCredentialMigrationResult(succeeded, AlreadyCompleted: false, migrated);
    }

    private async Task<bool> MigrateSourceAsync(
        LegacySource source,
        List<string> migrated,
        CancellationToken cancellationToken)
    {
        var legacy = await _credentials.GetAsync(source.Service, source.Account, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return false;
        }

        var targetReady = await MigrateSecretAsync(source.TargetAccount, legacy, migrated, cancellationToken)
            .ConfigureAwait(false);
        if (!targetReady)
        {
            // The old value is unusable (empty or malformed); leave it untouched and
            // let the user save a fresh key rather than deleting evidence.
            _logger?.LogWarning(
                "A legacy provider key for {Account} was unusable and was left untouched.",
                source.Account);
            return false;
        }

        await DeleteLegacyAsync(source, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Copies one secret when the target is empty. Returns true when the target is
    /// known to hold a usable key afterwards (copied now or already present), so a
    /// plaintext fallback for the same account cannot replace it.
    /// </summary>
    private async Task<bool> MigrateSecretAsync(
        string targetAccount,
        string legacySecret,
        List<string> migrated,
        CancellationToken cancellationToken)
    {
        var existing = await _credentials.GetAsync(CanonicalService, targetAccount, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return true;
        }

        var secret = NormalizeLegacySecret(legacySecret);
        if (secret is null)
        {
            return false;
        }

        await _credentials.SetAsync(CanonicalService, targetAccount, secret, cancellationToken)
            .ConfigureAwait(false);
        var stored = await _credentials.GetAsync(CanonicalService, targetAccount, cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeEquals(secret, stored))
        {
            // Never let a bad read-back become the key the next start trusts: drop
            // the copy we just wrote, keep the legacy source, and retry next start.
            try
            {
                await _credentials.DeleteAsync(CanonicalService, targetAccount, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The source stays; the marker stays unwritten so the next start retries.
            }

            throw new InvalidDataException("legacy credential read-back did not match");
        }

        migrated.Add(targetAccount);
        _logger?.LogInformation(
            "Migrated a legacy provider key into the {Account} account.",
            targetAccount);
        return true;
    }

    private async Task DeleteLegacyAsync(LegacySource source, CancellationToken cancellationToken)
    {
        // Never delete the canonical entry itself; it is the copy being kept.
        if (string.Equals(source.Service, CanonicalService, StringComparison.Ordinal)
            && string.Equals(source.Account, source.TargetAccount, StringComparison.Ordinal))
        {
            return;
        }

        await _credentials.DeleteAsync(source.Service, source.Account, cancellationToken)
            .ConfigureAwait(false);
    }

    private void WriteMarker()
    {
        var directory = Path.GetDirectoryName(_markerPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("legacy credential marker has no parent directory");
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{MarkerFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = JsonSerializer.Serialize(new
            {
                version = 1,
                completed_at = _clock.GetUtcNow().ToUniversalTime().ToString("O"),
            });
            File.WriteAllText(temporary, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _markerPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, string>? ReadLegacySettings()
    {
        if (_legacySettingsFile is null || !File.Exists(_legacySettingsFile))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(_legacySettingsFile);
            if (bytes.Length > 1024 * 1024)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, _) in LegacySettingsFields)
            {
                if (document.RootElement.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } secret)
                {
                    values[field] = secret;
                }
            }

            return values;
        }
        catch (JsonException)
        {
            // The old app ignored a malformed settings file; a key it never used is
            // not a migration failure. The credential-manager sources still run.
            return null;
        }
    }

    private static string? NormalizeLegacySecret(string? secret)
    {
        // The Python store stripped the key on save; keep that meaning and reject
        // values Credential Manager or an HTTP header cannot carry.
        var value = secret?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumSecretCharacters
            || value.Any(char.IsControl))
        {
            return null;
        }

        return value;
    }

    private static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (expected is null || actual is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }

    private static string? LegacySettingsPath(string? userProfile) =>
        string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, LegacySettingsRelativePath);
}
