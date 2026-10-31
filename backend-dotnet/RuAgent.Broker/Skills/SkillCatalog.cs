using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public sealed record SkillCatalogItem(string Id, string Name, string Description, string SourcePath, string ResourceBase,
    string Revision, bool Enabled, bool ModelInvocable, bool UserInvocable, IReadOnlyList<string> Requires, string? Compatibility);
public sealed record SkillCatalogDiagnostic(string SourcePath, string Code);
public sealed record SkillCatalogResult(IReadOnlyList<SkillCatalogItem> Items, IReadOnlyList<SkillCatalogDiagnostic> Diagnostics, bool Truncated, int Total);
public sealed record SkillPreferenceResult(bool Success, string Code, string Message);
public sealed record SkillBody(string Name, string Description, string Body, string BodySha256, string SourcePath,
    IReadOnlyList<string> Requires, string Id = "", string ResourceBase = "", string Revision = "",
    bool ModelInvocable = true, bool UserInvocable = true, string? Compatibility = null);

/// <summary>Workspace-scoped, metadata-only skill catalog and bounded on-demand body reader.</summary>
public sealed partial class SkillCatalog
{
    private const int MaxRoots = 16, MaxDirectoriesPerRoot = 256, MaxItems = 128, MaxDiagnostics = 128, MaxBodyBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _workspaceRoot;
    private readonly string _preferencesPath;
    private readonly List<string> _baseRoots;
    private readonly List<string> _roots;
    private readonly object _rootsGate = new();
    private string _cacheKey;

    public SkillCatalog(string workspaceRoot, string? preferencesPath = null, IReadOnlyList<string>? roots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = new CanonicalPathResolver(Path.GetFullPath(workspaceRoot)).CanonicalRoot;
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot)) appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(appDataRoot)) appDataRoot = Path.GetTempPath();
        _preferencesPath = Path.GetFullPath(preferencesPath ?? Path.Combine(appDataRoot, "Ru", "skill-preferences.json"));
        _baseRoots = (roots ?? SkillRoots.For(_workspaceRoot)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRoots).ToList();
        _roots = new(_baseRoots);
        _cacheKey = BuildCacheKey(_roots);
        RefreshRoots();
    }

    public SkillCatalogResult Discover(string? query = null, bool refresh = false, CancellationToken cancellationToken = default) => DiscoverCore(query, refresh, cancellationToken);
    public Task<SkillCatalogResult> DiscoverAsync(string? query = null, bool refresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(DiscoverCore(query, refresh, cancellationToken));

    public SkillCatalogItem? FindById(string id, CancellationToken cancellationToken = default)
    {
        if (!SkillIdentity.IsValid(id)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        RefreshRoots();
        var preferences = ReadPreferences();
        string[] roots; lock (_rootsGate) roots = _roots.ToArray();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = FindPackageDirectory(root, id, cancellationToken);
            if (directory is null) continue;
            var file = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(file)) continue;
            try
            {
                var resolved = new CanonicalPathResolver(root).Resolve(file);
                if (!resolved.IsInsideWorkspace) return null;
                file = resolved.CanonicalPath;
                directory = Path.GetDirectoryName(file)!;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
            var metadata = ReadMetadata(file);
            if (metadata is null) return null;
            var parsed = metadata.Value.Metadata;
            return new SkillCatalogItem(id, parsed.Name, parsed.Description, file, directory, metadata.Value.Revision,
                !preferences.Enabled.TryGetValue(id, out var enabled) || enabled,
                parsed.ModelInvocable && (!preferences.Implicit.TryGetValue(id, out var implicitValue) || implicitValue),
                parsed.UserInvocable, parsed.Requires, parsed.Compatibility);
        }
        return null;
    }

    public SkillBody? LoadBody(string id, string? expectedRevision = null, string? expectedBodySha256 = null, bool allowExplicit = false) =>
        TryLoadBody(id, out var body, out _, expectedRevision, expectedBodySha256, allowExplicit) ? body : null;

    public bool TryLoadBody(string id, out SkillBody? body, out string? errorCode, string? expectedRevision = null,
        string? expectedBodySha256 = null, bool allowExplicit = false)
    {
        body = null;
        errorCode = null;
        if (!SkillIdentity.IsValid(id)) { errorCode = "invalid_skill_id"; return false; }
        var item = FindById(id);
        if (item is null) { errorCode = "skill_not_found"; return false; }
        if (!item.Enabled) { errorCode = "skill_disabled"; return false; }
        if (!item.ModelInvocable && !allowExplicit) { errorCode = "skill_manual_only"; return false; }
        if (allowExplicit && !item.UserInvocable) { errorCode = "skill_user_invocation_disabled"; return false; }
        var current = ReadMetadata(item.SourcePath);
        if (current is null) { errorCode = "skill_not_found"; return false; }
        if (!string.Equals(item.Revision, current.Value.Revision, StringComparison.Ordinal)
            || (expectedRevision is not null && !string.Equals(expectedRevision, current.Value.Revision, StringComparison.Ordinal)))
        { errorCode = "skill_revision_changed"; return false; }
        var directory = Path.GetDirectoryName(item.SourcePath)!;
        try
        {
            if (HasReparseAncestor(item.SourcePath)) { errorCode = "skill_path_changed"; return false; }
            using var stream = HardLinkGuard.OpenRead(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, item.SourcePath);
            var bytes = new byte[MaxBodyBytes + 1]; var count = 0;
            while (count < bytes.Length) { var read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
            if (count > MaxBodyBytes) { errorCode = "skill_body_oversize"; return false; }
            var content = StrictUtf8.GetString(bytes, 0, count).TrimStart('\uFEFF').Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (content.Contains('\0')) { errorCode = "skill_body_contains_nul"; return false; }
            var parsed = SkillMetadataParser.Parse(content, id);
            if (parsed.Diagnostics.Contains("invalid_frontmatter", StringComparer.Ordinal)
                || parsed.Diagnostics.Contains("frontmatter_unclosed", StringComparer.Ordinal)
                || parsed.Diagnostics.Contains("frontmatter_oversize", StringComparer.Ordinal)) { errorCode = "skill_frontmatter_invalid"; return false; }
            if ((!allowExplicit && !parsed.ModelInvocable) || (allowExplicit && !parsed.UserInvocable)) { errorCode = "skill_invocation_disabled"; return false; }
            var afterRead = ReadMetadata(item.SourcePath);
            if (afterRead is null || !string.Equals(afterRead.Value.Revision, current.Value.Revision, StringComparison.Ordinal)) { errorCode = "skill_revision_changed"; return false; }
            var hash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(parsed.Body))).ToLowerInvariant();
            if (expectedBodySha256 is not null && !string.Equals(hash, expectedBodySha256, StringComparison.OrdinalIgnoreCase)) { errorCode = "skill_digest_mismatch"; return false; }
            body = new SkillBody(parsed.Name, parsed.Description, parsed.Body, hash, item.SourcePath, parsed.Requires,
                id, directory, current.Value.Revision, parsed.ModelInvocable, parsed.UserInvocable, parsed.Compatibility);
            return true;
        }
        catch (DecoderFallbackException) { errorCode = "skill_body_invalid_encoding"; return false; }
        catch (UnauthorizedAccessException) { errorCode = "skill_path_changed"; return false; }
        catch (Exception error) when (error is IOException or ArgumentException) { errorCode = "skill_not_found"; return false; }
    }

    private SkillCatalogResult DiscoverCore(string? query, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshRoots();
        string[] roots;
        lock (_rootsGate) roots = _roots.ToArray();
        var cacheKey = BuildCacheKey(roots);
        if (!refresh && query is null && SharedCache.TryGetValue(cacheKey, out var cached)
            && IsFresh(cached, roots, cancellationToken)) return cached.Result;
        var preferences = ReadPreferences();
        var diagnostics = new List<SkillCatalogDiagnostic>(); var items = new List<SkillCatalogItem>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false; var total = 0;
        if (query is not null && SkillIdentity.IsValid(query.ToLowerInvariant()))
        {
            var exact = FindById(query.ToLowerInvariant(), cancellationToken);
            if (exact is not null)
            {
                var unmet = SkillRequirements.FindUnmet(exact.Requires, SkillCapabilityAvailability.IsAvailable);
                if (unmet is not null) diagnostics.Add(new(exact.SourcePath, unmet.Code));
                return new SkillCatalogResult([exact], diagnostics, false, 1);
            }
        }
        foreach (var rootPath in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(rootPath)) continue;
            if (HasReparseAncestor(rootPath)) { AddDiagnostic(rootPath, "reparse_point"); continue; }
            var scan = ScanRoot(rootPath, cancellationToken);
            truncated |= scan.Truncated;
            foreach (var diagnostic in scan.Diagnostics) AddDiagnostic(diagnostic.SourcePath, diagnostic.Code);
            foreach (var discoveredDirectory in scan.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasReparseAncestor(discoveredDirectory)) { AddDiagnostic(discoveredDirectory, "reparse_point"); continue; }
                var file = Path.Combine(discoveredDirectory, "SKILL.md"); if (!File.Exists(file)) continue;
                string canonicalFile;
                try
                {
                    var resolved = new CanonicalPathResolver(rootPath).Resolve(file);
                    if (!resolved.IsInsideWorkspace) { AddDiagnostic(file, "outside_root"); continue; }
                    canonicalFile = resolved.CanonicalPath;
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { AddDiagnostic(file, "unreadable"); continue; }
                var directory = Path.GetDirectoryName(canonicalFile)!;
                file = canonicalFile;
                var id = Path.GetFileName(directory).ToLowerInvariant();
                if (!SkillIdentity.IsValid(id)) { AddDiagnostic(file, "invalid_skill_id"); continue; }
                if (seen.Contains(id)) { AddDiagnostic(file, "duplicate"); continue; }
                var metadata = ReadMetadata(file, out var metadataError);
                if (metadata is null) { AddDiagnostic(file, metadataError ?? "unreadable_or_invalid"); continue; }
                var parsedMetadata = metadata.Value.Metadata;
                seen.Add(id); total++;
                if (query is not null && !(parsedMetadata.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || parsedMetadata.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || id.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
                if (items.Count >= MaxItems) { truncated = true; continue; }
                items.Add(new SkillCatalogItem(id, parsedMetadata.Name, parsedMetadata.Description, file, directory, metadata.Value.Revision,
                    !preferences.Enabled.TryGetValue(id, out var enabled) || enabled,
                    parsedMetadata.ModelInvocable && (!preferences.Implicit.TryGetValue(id, out var implicitValue) || implicitValue), parsedMetadata.UserInvocable,
                    parsedMetadata.Requires, parsedMetadata.Compatibility));
                foreach (var code in parsedMetadata.Diagnostics) AddDiagnostic(file, code);
                var unmet = SkillRequirements.FindUnmet(parsedMetadata.Requires, SkillCapabilityAvailability.IsAvailable);
                if (unmet is not null) AddDiagnostic(file, unmet.Code);
            }
        }
        var result = new SkillCatalogResult(items, diagnostics, truncated, total);
        if (items.Count >= MaxItems) { truncated = true; AddDiagnostic("skill-catalog", "item_limit"); result = new SkillCatalogResult(items, diagnostics, truncated, total); }
        if (query is null)
        {
            StoreCache(cacheKey, CreateCacheEntry(result, roots, cancellationToken));
        }
        return result;
        void AddDiagnostic(string source, string code) { if (diagnostics.Count < MaxDiagnostics) diagnostics.Add(new(source, code)); else truncated = true; }
    }

}
