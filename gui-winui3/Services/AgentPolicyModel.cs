using System;
using System.Text.Json;

namespace RuApp.Services;

public sealed record AgentPolicyModel
{
    private const int MaxIdLength = 128;
    private const int MaxFieldLength = 256;

    private static readonly HashSet<string> LegacyFields = new(StringComparer.Ordinal)
    {
        "id", "scope", "kind", "description", "enabled", "version",
    };

    private static readonly HashSet<string> WireFields = new(StringComparer.Ordinal)
    {
        "rule_id", "scope", "decision", "matcher", "creation_order", "expires_at", "version",
    };

    public string Id { get; }
    public string Scope { get; }
    public string Kind { get; }
    public string Description { get; }
    public bool Enabled { get; }
    public long Version { get; }
    public JsonElement Matcher { get; }

    private static readonly JsonElement EmptyMatcherElement =
        JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>An empty matcher object (the API's wildcard rule).</summary>
    internal static JsonElement EmptyMatcher() => EmptyMatcherElement.Clone();

    public AgentPolicyModel(string id, string scope, string kind, string description, bool enabled, long version)
        : this(
            id,
            scope,
            kind,
            description,
            enabled,
            version,
            EmptyMatcher())
    {
    }

    private AgentPolicyModel(
        string id,
        string scope,
        string kind,
        string description,
        bool enabled,
        long version,
        JsonElement matcher)
    {
        Id = ValidateId(id);
        Scope = ValidateField(scope, nameof(scope));
        Kind = ValidateField(kind, nameof(kind));
        Description = ValidateField(description, nameof(description));
        Enabled = enabled;
        Version = version;
        Matcher = matcher.ValueKind == JsonValueKind.Object
            ? matcher.Clone()
            : throw new ArgumentException("Policy matcher is invalid.", nameof(matcher));
    }

    public static bool TryParse(JsonElement root, out AgentPolicyModel policy)
    {
        policy = null!;
        try
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                if (!observed.Add(prop.Name))
                {
                    return false;
                }
            }

            if (root.TryGetProperty("rule_id", out _))
            {
                return TryParseWire(root, out policy);
            }

            return TryParseLegacy(root, out policy);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParseWire(JsonElement root, out AgentPolicyModel policy)
    {
        policy = null!;
        foreach (var prop in root.EnumerateObject())
        {
            if (!WireFields.Contains(prop.Name))
            {
                return false;
            }
        }

        if (!root.TryGetProperty("rule_id", out var idEl)
            || idEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idEl.GetString())
            || !root.TryGetProperty("scope", out var scopeEl)
            || scopeEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("decision", out var decisionEl)
            || decisionEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("matcher", out var matcherEl)
            || matcherEl.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionEl)
            || versionEl.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var id = idEl.GetString()!;
        var scope = scopeEl.GetString()!;
        var decision = decisionEl.GetString()!;
        var version = versionEl.GetInt64();
        var enabled = true;
        if (root.TryGetProperty("expires_at", out var expires)
            && expires.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(expires.GetString(), out var expiry))
        {
            enabled = expiry > DateTimeOffset.UtcNow;
        }

        policy = new AgentPolicyModel(
            id,
            scope,
            decision,
            DescribeMatcher(matcherEl),
            enabled,
            version,
            matcherEl);
        return true;
    }

    private static bool TryParseLegacy(JsonElement root, out AgentPolicyModel policy)
    {
        policy = null!;
        foreach (var prop in root.EnumerateObject())
        {
            if (!LegacyFields.Contains(prop.Name))
            {
                return false;
            }
        }

        if (!root.TryGetProperty("id", out var idEl)
            || idEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idEl.GetString())
            || !root.TryGetProperty("scope", out var sEl)
            || sEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("kind", out var kEl)
            || kEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("description", out var dEl)
            || dEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("enabled", out var eEl)
            || (eEl.ValueKind != JsonValueKind.True && eEl.ValueKind != JsonValueKind.False)
            || !root.TryGetProperty("version", out var vEl)
            || vEl.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var id = idEl.GetString()!;
        var scope = sEl.GetString()!;
        var kind = kEl.GetString()!;
        var description = dEl.GetString()!;
        var enabled = eEl.ValueKind == JsonValueKind.True;
        var version = vEl.GetInt64();

        policy = new AgentPolicyModel(id, scope, kind, description, enabled, version);
        return true;
    }

    private static string DescribeMatcher(JsonElement matcher)
    {
        var values = new List<string>();
        if (matcher.TryGetProperty("tool_id", out var toolId)
            && toolId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(toolId.GetString()))
        {
            values.Add(toolId.GetString()!);
        }

        if (matcher.TryGetProperty("canonical_path_pattern", out var path)
            && path.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(path.GetString()))
        {
            values.Add(path.GetString()!);
        }

        return string.Join(" · ", values);
    }

    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > MaxIdLength
            || !string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Policy id is invalid.", nameof(id));
        }

        return id;
    }

    private static string ValidateField(string value, string name)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value.Length > MaxFieldLength || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Policy {name} is invalid.", name);
        }

        return value;
    }
}
