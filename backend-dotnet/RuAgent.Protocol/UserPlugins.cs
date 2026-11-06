using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RuAgent.Protocol;

/// <summary>A plugin's <c>ru-plugin.json</c> (ADR 0044).</summary>
public sealed record UserPluginManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("version")] string? Version = null);

public enum UserPluginSource
{
    /// <summary><c>~/.ru/plugins/&lt;id&gt;</c>: written by the user or created in Customize.</summary>
    Local,
    /// <summary><c>%LOCALAPPDATA%\Ru\plugins\&lt;id&gt;</c>: installed and managed by Ru.</summary>
    Managed,
}

/// <summary>One plugin folder as found on disk, with what it contains and whether the user enabled it.</summary>
public sealed record UserPlugin(
    UserPluginManifest Manifest,
    string Directory,
    UserPluginSource Source,
    bool Enabled,
    bool HasSkills,
    bool HasInstructions,
    bool HasHooks,
    bool HasMcpServers,
    IReadOnlyList<string> Diagnostics)
{
    public string Id => Manifest.Id;
    public string SkillsDirectory => Path.Combine(Directory, "skills");
    public string InstructionsFile => Path.Combine(Directory, "AGENTS.md");
}

/// <summary>
/// User plugins (ADR 0044): folders under <c>~/.ru/plugins</c> and
/// <c>%LOCALAPPDATA%\Ru\plugins</c>, each with a strict <c>ru-plugin.json</c>. A plugin is
/// off until the user enables it; the enabled set lives in Ru's own data folder, never in a
/// plugin. Enabled plugins add their <c>skills/</c> after the user skill roots and their
/// <c>AGENTS.md</c> after the user instructions, so they have the lowest precedence.
/// Hooks and MCP servers in a plugin are recognized but not loaded yet.
/// </summary>
public static partial class UserPlugins
{
    public const string ManifestFileName = "ru-plugin.json";
    public const int MaxPlugins = 64;
    /// <summary>Enabled plugins that may add a skill root: the skill catalog caps roots at 16.</summary>
    public const int MaxSkillPlugins = 8;
    public const int MaxManifestBytes = 16 * 1024;
    public const int MaxNameChars = 64;
    public const int MaxDescriptionChars = 512;
    public const int MaxVersionChars = 32;
    private const int MaxPreferenceBytes = 64 * 1024;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };

    [GeneratedRegex("^[a-z][a-z0-9-]{0,23}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && !id.EndsWith('-');

    public static string LocalRoot(string? userProfileRoot = null) =>
        Path.Combine(userProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ru", "plugins");

    public static string ManagedRoot(string? localApplicationDataRoot = null) =>
        Path.Combine(localApplicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "plugins");

    public static string PreferencesPath(string? localApplicationDataRoot = null) =>
        Path.Combine(localApplicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "plugin-preferences.json");

    /// <summary>
    /// Every plugin folder in both roots. A local plugin wins over a managed one with the
    /// same id; the loser is reported, not loaded. Folders that are links, have no or an
    /// invalid manifest, or whose manifest id differs from the folder name are skipped
    /// and reported through <paramref name="skipped"/>.
    /// </summary>
    public static IReadOnlyList<UserPlugin> Discover(
        string? localApplicationDataRoot = null,
        string? userProfileRoot = null,
        List<string>? skipped = null)
    {
        var enabled = ReadEnabled(localApplicationDataRoot);
        var found = new List<UserPlugin>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, source) in new[]
                 {
                     (LocalRoot(userProfileRoot), UserPluginSource.Local),
                     (ManagedRoot(localApplicationDataRoot), UserPluginSource.Managed),
                 })
        {
            if (!System.IO.Directory.Exists(root) || IsLink(new DirectoryInfo(root))) continue;
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (found.Count >= MaxPlugins)
                {
                    skipped?.Add($"{folder.Name}: more than {MaxPlugins} plugins; the rest are not loaded.");
                    return found;
                }

                if (folder.Name.StartsWith('.')) continue;
                if (IsLink(folder))
                {
                    skipped?.Add($"{folder.Name}: the folder is a link, which Ru does not follow.");
                    continue;
                }

                var manifest = ReadManifest(folder.FullName, out var problem);
                if (manifest is null)
                {
                    skipped?.Add($"{folder.Name}: {problem}");
                    continue;
                }

                if (!ids.Add(manifest.Id))
                {
                    skipped?.Add($"{folder.Name}: a plugin named {manifest.Id} is already loaded from {LocalRoot(userProfileRoot)}.");
                    continue;
                }

                found.Add(Describe(manifest, folder.FullName, source, enabled.Contains(manifest.Id)));
            }
        }

        return found;
    }

    /// <summary>The enabled plugins' skill folders, in plugin order, at most <see cref="MaxSkillPlugins"/>.</summary>
    public static IReadOnlyList<string> EnabledSkillRoots(string? localApplicationDataRoot = null, string? userProfileRoot = null)
    {
        try
        {
            return Discover(localApplicationDataRoot, userProfileRoot)
                .Where(plugin => plugin.Enabled && plugin.HasSkills)
                .Take(MaxSkillPlugins)
                .Select(plugin => plugin.SkillsDirectory)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A plugin folder Ru cannot read adds no skills; it never blocks the user's own roots.
            return Array.Empty<string>();
        }
    }

    public static UserPluginManifest? ReadManifest(string pluginDirectory, out string problem)
    {
        problem = string.Empty;
        var path = Path.Combine(pluginDirectory, ManifestFileName);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            problem = $"no {ManifestFileName}.";
            return null;
        }

        if (IsLink(info) || info.Length > MaxManifestBytes)
        {
            problem = $"{ManifestFileName} is a link or larger than {MaxManifestBytes / 1024} KB.";
            return null;
        }

        UserPluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UserPluginManifest>(File.ReadAllBytes(path), StrictJson);
        }
        catch (JsonException)
        {
            problem = $"{ManifestFileName} is not valid: it needs id and name, and no other fields than description and version.";
            return null;
        }

        var folder = Path.GetFileName(pluginDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (manifest is null || !IsValidId(manifest.Id))
            problem = "the id must be 1 to 24 lowercase letters, digits or dashes, starting with a letter.";
        else if (!string.Equals(manifest.Id, folder, StringComparison.Ordinal))
            problem = $"the id {manifest.Id} does not match the folder name {folder}.";
        else if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > MaxNameChars)
            problem = $"the name must be 1 to {MaxNameChars} characters.";
        else if (manifest.Description?.Length > MaxDescriptionChars || manifest.Version?.Length > MaxVersionChars)
            problem = "the description or version is too long.";
        return problem.Length == 0 ? manifest : null;
    }

    public static IReadOnlySet<string> ReadEnabled(string? localApplicationDataRoot = null)
    {
        var path = PreferencesPath(localApplicationDataRoot);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPreferenceBytes) return new HashSet<string>(StringComparer.Ordinal);
            var preferences = JsonSerializer.Deserialize<PluginPreferences>(File.ReadAllBytes(path));
            return new HashSet<string>((preferences?.Enabled ?? []).Where(IsValidId), StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable preferences enable nothing: plugins stay off rather than on by accident.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Turns one plugin on or off. Writes are atomic and serialized across processes.</summary>
    public static void SetEnabled(string id, bool enabled, string? localApplicationDataRoot = null)
    {
        if (!IsValidId(id)) throw new ArgumentException("invalid_plugin_id", nameof(id));
        var path = PreferencesPath(localApplicationDataRoot);
        using var mutex = new Mutex(false, "Ru.PluginPreferences." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16]);
        try { mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { }
        try
        {
            var set = new SortedSet<string>(ReadEnabled(localApplicationDataRoot), StringComparer.Ordinal);
            if (enabled) set.Add(id);
            else set.Remove(id);
            WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(new PluginPreferences(set.ToArray()), WriteJson));
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    /// <summary>
    /// Creates a new local plugin folder with its manifest and, as asked, an <c>AGENTS.md</c>
    /// and an example skill. Never overwrites: an existing folder fails.
    /// </summary>
    public static UserPlugin Create(
        string id, string name, string? description, bool withInstructions, bool withSkill,
        string? localApplicationDataRoot = null, string? userProfileRoot = null)
    {
        if (!IsValidId(id)) throw new ArgumentException("invalid_plugin_id", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameChars) throw new ArgumentException("invalid_plugin_name", nameof(name));
        if (description?.Length > MaxDescriptionChars) throw new ArgumentException("invalid_plugin_description", nameof(description));

        var root = LocalRoot(userProfileRoot);
        if (System.IO.Directory.Exists(root) && IsLink(new DirectoryInfo(root))) throw new IOException("plugin_root_is_link");
        var directory = Path.Combine(root, id);
        if (System.IO.Directory.Exists(directory) || System.IO.Directory.Exists(Path.Combine(ManagedRoot(localApplicationDataRoot), id)))
            throw new IOException("plugin_exists");

        var manifest = new UserPluginManifest(id, name.Trim(), string.IsNullOrWhiteSpace(description) ? null : description.Trim(), "0.1.0");
        System.IO.Directory.CreateDirectory(directory);
        WriteAtomically(Path.Combine(directory, ManifestFileName), JsonSerializer.SerializeToUtf8Bytes(manifest, WriteJson));
        if (withInstructions)
        {
            File.WriteAllText(Path.Combine(directory, "AGENTS.md"),
                $"# {manifest.Name}\n\nGuidance Ru follows while this plugin is enabled.\n\n- Write your rules here.\n",
                new UTF8Encoding(false));
        }

        if (withSkill)
        {
            var skill = Path.Combine(directory, "skills", $"{id}-example");
            System.IO.Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"),
                $"---\nname: {id}-example\ndescription: Example workflow from the {manifest.Name} plugin. Replace this with your own.\n---\n\n# {manifest.Name} example\n\n1. Describe the steps Ru should follow.\n",
                new UTF8Encoding(false));
        }

        return Describe(manifest, directory, UserPluginSource.Local, ReadEnabled(localApplicationDataRoot).Contains(id));
    }

    private static UserPlugin Describe(UserPluginManifest manifest, string directory, UserPluginSource source, bool enabled)
    {
        bool hasSkills = HasEntries(Path.Combine(directory, "skills"));
        bool hasInstructions = File.Exists(Path.Combine(directory, "AGENTS.md"));
        bool hasHooks = File.Exists(Path.Combine(directory, "hooks.json"));
        bool hasMcp = File.Exists(Path.Combine(directory, "mcp.json"));
        var diagnostics = new List<string>();
        if (hasHooks || hasMcp)
            diagnostics.Add("Hooks and MCP servers in plugins are not loaded yet; this plugin's skills and instructions work.");
        if (!hasSkills && !hasInstructions && !hasHooks && !hasMcp)
            diagnostics.Add("This plugin is empty: add a skills folder or an AGENTS.md.");
        return new UserPlugin(manifest, directory, source, enabled, hasSkills, hasInstructions, hasHooks, hasMcp, diagnostics);
    }

    private static bool HasEntries(string directory)
    {
        var info = new DirectoryInfo(directory);
        return info.Exists && !IsLink(info) && info.EnumerateFileSystemInfos().Any();
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record PluginPreferences([property: JsonPropertyName("enabled")] string[]? Enabled);
}
