using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Skills;

public sealed partial class SkillPackageInstaller
{
    private string CreateTransactionDirectory()
    {
        var transactionRoot = Path.Combine(_installRoot, ".ru-skill-install-" + Guid.NewGuid().ToString("N"));
        EnsureContained(_installRoot, transactionRoot);
        Directory.CreateDirectory(transactionRoot);
        return transactionRoot;
    }

    private async Task<FileStream> AcquireInstallLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_installRoot, ".ru-skill-install.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw Failure("skill_install_root_unsafe", "The skill installation lock path is linked.");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                throw Failure("skill_install_busy", "Another skill package update is in progress; retry shortly.", error);
            }
        }
    }

    private void EnsureSafeRoot()
    {
        var current = _installRoot;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Failure("skill_install_root_unsafe", "The managed skill installation root contains a linked directory.");
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
    }

    private static void EnsureNoReparsePoints(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw Failure("skill_target_unmanaged", "The existing package contains a linked directory.");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw Failure("skill_target_unmanaged", "The existing package contains a linked file.");
    }

    private static void CreateSafeDirectories(string root, string destination)
    {
        // A file directly in the staging root needs no child directories. Keep
        // EnsureContained strict for archive entries, which must be descendants.
        if (string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw Failure("skill_archive_invalid", "The extraction root is linked.");
            return;
        }
        EnsureContained(root, destination);
        var relative = Path.GetRelativePath(root, destination);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Failure("skill_archive_invalid", "An extracted package path is linked.");
            }
            else Directory.CreateDirectory(current);
        }
    }

    private static string ContainedPath(string root, string relative)
    {
        ValidateRelativePath(relative.Replace(Path.DirectorySeparatorChar, '/'));
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        EnsureContained(fullRoot, full);
        return full;
    }

    private static void EnsureContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Failure("skill_archive_invalid", "A package path resolved outside its managed root.");
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.StartsWith('/') || path.StartsWith('\\')
            || path.Contains('\\') || Path.IsPathRooted(path))
            throw Failure("skill_archive_invalid", "The archive contains an absolute or malformed path.");
        foreach (var segment in path.Split('/'))
        {
            var stem = segment.Split('.')[0];
            if (segment is "" or "." or ".." || segment.Any(c => "<>:\"|?*".Contains(c))
                || segment.Any(char.IsControl) || segment.EndsWith(' ') || segment.EndsWith('.')
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw Failure("skill_archive_invalid", "The archive contains an unsafe path segment.");
        }
    }

    private static void ValidateRelativePath(string path) => ValidateArchivePath(path);

    private static string PackageDigest(IReadOnlyList<StagedFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            hash.AppendData(StrictUtf8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(StrictUtf8.GetBytes(file.Sha256));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> ParentDirectories(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        while (slash > 0)
        {
            yield return relativePath[..slash];
            slash = relativePath.LastIndexOf('/', slash - 1);
        }
    }

    private static void DeleteOwnedTransaction(string installRoot, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var expectedParent = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parent is null || !Regex.IsMatch(Path.GetFileName(full), "^\\.ru-skill-install-[a-f0-9]{32}$", RegexOptions.CultureInvariant)
                || !string.Equals(parent, expectedParent,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            if (Directory.Exists(full))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
                EnsureNoReparsePoints(full);
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

}
