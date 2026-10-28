using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public enum SkillPackageInstallAction { Install, Update }

public sealed record SkillPackageInstallReceipt(
    string Action,
    string Id,
    string Name,
    string Description,
    string Source,
    string Commit,
    string PackageSha256,
    int FileCount,
    long ExpandedBytes,
    string InstallPath);

public sealed class SkillPackageInstallException : Exception
{
    public SkillPackageInstallException(string code, string message) : base(message) => Code = code;
    public SkillPackageInstallException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
    public string Code { get; }
}

/// <summary>Installs an explicitly selected, commit-pinned GitHub skill package under one managed user root.</summary>
public sealed partial class SkillPackageInstaller : IDisposable
{
    public const string ManifestFileName = ".ru-skill-package.json";
    public const int MaxArchiveBytes = 16 * 1024 * 1024;
    public const long MaxExpandedBytes = 64 * 1024 * 1024;
    public const long MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxFiles = 256;
    private const int MaxArchiveEntries = 4096;
    private const int MaxManifestBytes = 128 * 1024;
    private const int MaxResponseReadBytes = MaxArchiveBytes + 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _installRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public SkillPackageInstaller(string installRoot, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        _installRoot = Path.GetFullPath(installRoot);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public async Task<SkillPackageInstallReceipt> InstallAsync(
        string source, SkillPackageInstallAction action, CancellationToken cancellationToken = default)
    {
        if (action is not (SkillPackageInstallAction.Install or SkillPackageInstallAction.Update))
            throw Failure("invalid_skill_action", "Choose explicit install or update.");
        var parsed = ParseSource(source);
        var gate = RootGates.GetOrAdd(_installRoot, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? installLock = null;
        string? transactionRoot = null;
        try
        {
            Directory.CreateDirectory(_installRoot);
            EnsureSafeRoot();
            installLock = await AcquireInstallLockAsync(cancellationToken).ConfigureAwait(false);
            var target = ContainedPath(_installRoot, parsed.Id);
            if (action == SkillPackageInstallAction.Install && Directory.Exists(target))
                throw Failure("skill_target_exists", "A skill with this ID already exists; choose explicit update to replace a managed package.");
            if (File.Exists(target))
                throw Failure("skill_target_exists", "A file already occupies this skill ID.");
            if (action == SkillPackageInstallAction.Update)
                await VerifyManagedTargetAsync(target, parsed, cancellationToken).ConfigureAwait(false);

            transactionRoot = CreateTransactionDirectory();
            var archiveBytes = await DownloadArchiveAsync(parsed, cancellationToken).ConfigureAwait(false);
            var files = ExtractSelectedPackage(archiveBytes, parsed, Path.Combine(transactionRoot, "payload", parsed.Id));
            var skill = ParseSkillFile(parsed.Id, Path.Combine(transactionRoot, "payload", parsed.Id, "SKILL.md"));
            var digest = PackageDigest(files);
            var manifest = new PackageManifest(
                1, parsed.Id, parsed.Owner, parsed.Repository, parsed.Subdirectory, parsed.Commit,
                digest, files.Select(file => new ManagedFile(file.RelativePath, file.Length, file.Sha256)).ToArray());
            var packageDirectory = Path.Combine(transactionRoot, "payload", parsed.Id);
            await WriteManifestAsync(packageDirectory, manifest, cancellationToken).ConfigureAwait(false);

            var backup = Path.Combine(transactionRoot, "previous");
            var movedPrevious = false;
            if (action == SkillPackageInstallAction.Update)
            {
                // The download may take time; verify again immediately before moving the live target.
                await VerifyManagedTargetAsync(target, parsed, cancellationToken).ConfigureAwait(false);
                Directory.Move(target, backup);
                movedPrevious = true;
            }
            try
            {
                Directory.Move(packageDirectory, target);
            }
            catch
            {
                if (movedPrevious && !Directory.Exists(target) && Directory.Exists(backup))
                    Directory.Move(backup, target);
                throw;
            }

            // The transaction directory is generated, verified under the install root, and owns
            // only downloaded bytes plus a verified prior managed package.
            DeleteOwnedTransaction(_installRoot, transactionRoot);
            transactionRoot = null;
            return new(action == SkillPackageInstallAction.Install ? "installed" : "updated",
                parsed.Id, skill.Name, skill.Description, parsed.Source, parsed.Commit, digest,
                files.Count, files.Sum(file => file.Length), target);
        }
        catch (SkillPackageInstallException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            throw Failure("skill_install_failed", "The skill package could not be installed safely.", error);
        }
        finally
        {
            if (transactionRoot is not null) DeleteOwnedTransaction(_installRoot, transactionRoot);
            installLock?.Dispose();
            gate.Release();
        }
    }

    private async Task VerifyManagedTargetAsync(string target, PinnedSkillSource source, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target)) throw Failure("skill_update_target_missing", "Update requires an existing package installed by Ru.");
        try
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw Failure("skill_target_unmanaged", "The existing package directory is linked.");
            EnsureNoReparsePoints(target);
            var manifestPath = Path.Combine(target, ManifestFileName);
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length > MaxManifestBytes) throw Failure("skill_target_unmanaged", "The existing folder is not a recognized Ru-managed package.");
            var manifest = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (manifest is null || manifest.Version != 1 || manifest.Id != source.Id
                || !string.Equals(manifest.Owner, source.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifest.Repository, source.Repository, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifest.Subdirectory, source.Subdirectory, StringComparison.Ordinal))
                throw Failure("skill_target_unmanaged", "The existing package was installed from a different source or has an invalid manifest.");
            if (manifest.Files is null || manifest.Files.Count is < 1 or > MaxFiles
                || !Regex.IsMatch(manifest.Commit ?? string.Empty, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant)
                || !IsSha256(manifest.PackageSha256))
                throw Failure("skill_target_unmanaged", "The existing package manifest is invalid.");

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFileName };
            long total = 0;
            foreach (var file in manifest.Files)
            {
                ValidateRelativePath(file.Path);
                if (string.Equals(file.Path, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw Failure("skill_target_unmanaged", "The existing package manifest is invalid.");
                if (!expected.Add(file.Path) || file.Length < 0 || file.Length > MaxFileBytes || !IsSha256(file.Sha256))
                    throw Failure("skill_target_unmanaged", "The existing package manifest is invalid.");
                total += file.Length;
                if (total > MaxExpandedBytes) throw Failure("skill_target_unmanaged", "The existing package exceeds the package size limit.");
                var path = ContainedPath(target, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var current = new FileInfo(path);
                if (!current.Exists || current.Length != file.Length)
                    throw Failure("skill_target_modified", "The existing package was changed after installation.");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                    throw Failure("skill_target_modified", "The existing package was changed after installation.");
            }
            var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(target, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(expected))
                throw Failure("skill_target_modified", "The existing package contains files outside its installer manifest.");
            var expectedDirectories = manifest.Files.SelectMany(file => ParentDirectories(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualDirectories = Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(target, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actualDirectories.SetEquals(expectedDirectories))
                throw Failure("skill_target_modified", "The existing package contains directories outside its installer manifest.");
            var packageDigest = PackageDigest(manifest.Files.Select(file => new StagedFile(file.Path, file.Length, file.Sha256)).ToArray());
            if (!string.Equals(packageDigest, manifest.PackageSha256, StringComparison.Ordinal))
                throw Failure("skill_target_unmanaged", "The existing package manifest digest is invalid.");
        }
        catch (SkillPackageInstallException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw Failure("skill_target_unmanaged", "The existing package could not be verified as a Ru-managed package.", error);
        }
    }

    private static async Task WriteManifestAsync(string packageDirectory, PackageManifest manifest, CancellationToken cancellationToken)
    {
        var path = Path.Combine(packageDirectory, ManifestFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (bytes.Length > MaxManifestBytes) throw Failure("skill_archive_too_large", "The package manifest exceeds the storage limit.");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private PinnedSkillSource ParseSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Contains('%') || source.Contains('\\')
            || source.Contains("/../", StringComparison.Ordinal) || source.EndsWith("/..", StringComparison.Ordinal)
            || source.Contains("/./", StringComparison.Ordinal)
            || (source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                && source["https://github.com/".Length..].Contains("//", StringComparison.Ordinal)))
            throw Failure("invalid_skill_source", "Use an HTTPS GitHub tree URL pinned to a 40-character commit and package subdirectory.");
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw Failure("invalid_skill_source", "Only pinned HTTPS GitHub tree URLs are supported.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[2] != "tree" || !Regex.IsMatch(parts[3], "^[A-Fa-f0-9]{40}$", RegexOptions.CultureInvariant))
            throw Failure("invalid_skill_source", "Use https://github.com/{owner}/{repo}/tree/{40-hex-commit}/{package-folder}.");
        var owner = parts[0];
        var repo = parts[1];
        if (!SafeGitHubSegment.IsMatch(owner) || !SafePathSegment.IsMatch(repo) || repo is "." or "..")
            throw Failure("invalid_skill_source", "The GitHub owner or repository name is invalid.");
        var subdirectoryParts = parts.Skip(4).ToArray();
        if (subdirectoryParts.Any(part => part is "." or ".." || !SafePathSegment.IsMatch(part)))
            throw Failure("invalid_skill_source", "The package subdirectory contains an invalid path segment.");
        var subdirectory = string.Join('/', subdirectoryParts);
        var id = subdirectoryParts[^1].ToLowerInvariant();
        if (!SkillIdentity.IsValid(id)) throw Failure("invalid_skill_source", "The package folder name must be a valid Ru skill ID.");
        return new(owner, repo, subdirectory, parts[3].ToLowerInvariant(), id,
            $"https://github.com/{owner}/{repo}/tree/{parts[3].ToLowerInvariant()}/{subdirectory}");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static SkillPackageInstallException Failure(string code, string message, Exception? inner = null) =>
        inner is null ? new(code, message) : new SkillPackageInstallException(code, message, inner);

    private sealed record PinnedSkillSource(string Owner, string Repository, string Subdirectory, string Commit, string Id, string Source);
    private sealed record StagedFile(string RelativePath, long Length, string Sha256);
    private sealed record ManagedFile(string Path, long Length, string Sha256);
    private sealed record PackageManifest(int Version, string Id, string Owner, string Repository,
        string Subdirectory, string Commit, string PackageSha256, IReadOnlyList<ManagedFile> Files);
    private static readonly Regex SafeGitHubSegment = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafePathSegment = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
