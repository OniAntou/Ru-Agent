namespace RuAgent.Broker.Skills;

/// <summary>Compatibility facade for existing Broker callers; parsing and identity live in SkillCatalog.</summary>
public sealed class SkillBodyStore
{
    private readonly SkillCatalog _catalog;

    public SkillBodyStore(string workspaceRoot, string? preferencesPath = null, IReadOnlyList<string>? roots = null) =>
        _catalog = new SkillCatalog(workspaceRoot, preferencesPath, roots);

    public SkillBody? LoadBody(string name, string? expectedRevision = null, string? expectedBodySha256 = null, bool allowExplicit = false) =>
        _catalog.LoadBody(name, expectedRevision, expectedBodySha256, allowExplicit);

    public SkillCatalogResult Discover(string? query = null, bool refresh = false, CancellationToken cancellationToken = default) =>
        _catalog.Discover(query, refresh, cancellationToken);

    public bool TryLoadBody(string id, out SkillBody? body, out string? errorCode, string? expectedRevision = null,
        string? expectedBodySha256 = null, bool allowExplicit = false) =>
        _catalog.TryLoadBody(id, out body, out errorCode, expectedRevision, expectedBodySha256, allowExplicit);
}
