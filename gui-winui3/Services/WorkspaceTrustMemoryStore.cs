using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// Remembers the display identity hash last approved for a workspace path.
/// </summary>
/// <remarks>
/// The Host stays the trust authority; this file never answers a challenge. It
/// only lets the GUI tell a recreated or moved root ("trust this folder again")
/// apart from a first-seen folder, so the re-approval carries the identity-change
/// context instead of repeating the first-trust copy verbatim. A missing or
/// unreadable file reads as "never approved", which is the safe default.
/// </remarks>
public sealed class WorkspaceTrustMemoryStore
{
    /// <summary>
    /// A hard cap on remembered paths. A machine with more approved workspaces
    /// than this keeps the most recent ones; the rest ask as first-seen again.
    /// </summary>
    internal const int MaxEntries = 512;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public WorkspaceTrustMemoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "workspace-trust-memory.json");
    }

    public static string Canonicalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// True when this path was approved before with a different display identity
    /// hash: the root was moved, recreated, or its root files changed, so the
    /// earlier trust no longer covers the identity being challenged now.
    /// </summary>
    public bool IsReTrust(string path, string? displayIdentityHash)
    {
        if (string.IsNullOrWhiteSpace(displayIdentityHash))
        {
            return false;
        }

        var remembered = FindHash(path);
        return remembered is not null
            && !string.Equals(remembered, displayIdentityHash, StringComparison.Ordinal);
    }

    /// <summary>Records an approved identity for the path. Best-effort and bounded.</summary>
    public bool Remember(string path, string? displayIdentityHash)
    {
        if (string.IsNullOrWhiteSpace(displayIdentityHash) || TryCanonicalize(path) is not { } canonical)
        {
            return false;
        }

        var entries = Load();
        entries.RemoveAll(entry => string.Equals(entry.Path, canonical, StringComparison.OrdinalIgnoreCase));
        entries.Add(new Entry
        {
            Path = canonical,
            DisplayIdentityHash = displayIdentityHash,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
        });

        if (entries.Count > MaxEntries)
        {
            entries = entries
                .OrderByDescending(entry => entry.ApprovedAtUtc)
                .Take(MaxEntries)
                .ToList();
        }

        return TrySave(entries);
    }

    private string? FindHash(string path)
    {
        if (TryCanonicalize(path) is not { } canonical)
        {
            return null;
        }

        foreach (var entry in Load())
        {
            if (string.Equals(entry.Path, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return entry.DisplayIdentityHash;
            }
        }

        return null;
    }

    private static string? TryCanonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Canonicalize(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private List<Entry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return [];
            }

            var model = JsonSerializer.Deserialize<FileModel>(bytes, JsonOptions);
            return model?.Entries?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path)
                    && !string.IsNullOrWhiteSpace(entry.DisplayIdentityHash))
                .ToList()
                ?? [];
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return [];
        }
    }

    private bool TrySave(List<Entry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new FileModel { Entries = entries }, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private sealed class FileModel
    {
        public List<Entry>? Entries { get; set; }
    }

    private sealed class Entry
    {
        public string? Path { get; set; }

        public string? DisplayIdentityHash { get; set; }

        public DateTimeOffset ApprovedAtUtc { get; set; }
    }
}
