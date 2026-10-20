using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Policy;

public static class PolicySnapshotCanonicalizer
{
    public static PolicySnapshot Create(
        long version,
        AgentPermissionPreset preset,
        WorkspaceIdentity workspace,
        IReadOnlyList<PolicyRule> orderedRules,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(orderedRules);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (createdAt == default)
        {
            throw new ArgumentException("CreatedAt must be a valid timestamp.", nameof(createdAt));
        }

        foreach (var rule in orderedRules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            rule.Validate();
        }

        // CreatedAt is metadata only: it is not part of the hash bytes, because a
        // snapshot re-loaded after a restart carries a fresh timestamp while the
        // durable run stores the hash computed at creation time. Hashing the
        // timestamp made every recovery comparison fail and parked every
        // non-terminal run. The hash binds version, preset, workspace, and rules.
        var normalizedCreatedAt = createdAt.ToUniversalTime();
        var unhashed = new PolicySnapshot(
            version,
            preset,
            workspace,
            orderedRules,
            normalizedCreatedAt,
            string.Empty);
        var sha256 = Convert.ToHexString(SHA256.HashData(GetCanonicalUtf8(unhashed)))
            .ToLowerInvariant();

        return new PolicySnapshot(
            unhashed.Version,
            unhashed.Preset,
            unhashed.Workspace,
            unhashed.OrderedRules,
            unhashed.CreatedAt,
            sha256);
    }

    public static byte[] GetCanonicalUtf8(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshotForCanonicalization(snapshot);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions
                    {
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteString("preset", AgentPermissionPresetMapper.ToWireValue(snapshot.Preset));
            WriteWorkspace(writer, snapshot.Workspace);
            WriteRules(writer, snapshot.OrderedRules);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void ValidateSnapshotForCanonicalization(PolicySnapshot snapshot)
    {
        if (snapshot.Version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot.Version));
        }

        if (!Enum.IsDefined(snapshot.Preset))
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot.Preset));
        }

        ArgumentNullException.ThrowIfNull(snapshot.Workspace);
        _ = snapshot.Workspace.WorkspaceId.Value;
        _ = snapshot.Workspace.VolumeSerialNumber;
        _ = snapshot.Workspace.RootFileId;
        _ = snapshot.Workspace.DisplayIdentityHash;

        ArgumentNullException.ThrowIfNull(snapshot.OrderedRules);
        foreach (var rule in snapshot.OrderedRules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            rule.Validate();
        }

        if (snapshot.CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt must be a valid timestamp.", nameof(snapshot.CreatedAt));
        }

        // Sha256 may be empty for intermediate unhashed snapshot; otherwise must be 64 hex.
        if (snapshot.Sha256.Length != 0 && !IsSha256(snapshot.Sha256))
        {
            throw new ArgumentException("Sha256 must be empty or 64 hex.", nameof(snapshot.Sha256));
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static void WriteWorkspace(Utf8JsonWriter writer, WorkspaceIdentity workspace)
    {
        writer.WritePropertyName("workspace");
        writer.WriteStartObject();
        writer.WriteString("workspace_id", workspace.WorkspaceId.Value);
        writer.WriteString("volume_serial_number", workspace.VolumeSerialNumber);
        writer.WriteString("root_file_id", workspace.RootFileId);
        writer.WriteString("display_identity_hash", workspace.DisplayIdentityHash);
        writer.WriteEndObject();
    }

    private static void WriteRules(Utf8JsonWriter writer, IReadOnlyList<PolicyRule> rules)
    {
        writer.WritePropertyName("ordered_rules");
        writer.WriteStartArray();
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(rule.Matcher);

            writer.WriteStartObject();
            writer.WriteString("rule_id", rule.RuleId.Value);
            writer.WriteString("scope", ScopeName(rule.Scope));
            writer.WriteString("decision", DecisionName(rule.Decision));
            WriteMatcher(writer, rule.Matcher);
            writer.WriteNumber("creation_order", rule.CreationOrder);
            if (rule.ExpiresAt is { } expiresAt)
            {
                writer.WriteString(
                    "expires_at",
                    expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("expires_at");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMatcher(Utf8JsonWriter writer, PolicyMatcher matcher)
    {
        writer.WritePropertyName("matcher");
        writer.WriteStartObject();
        WriteOptionalString(writer, "tool_id", matcher.ToolId);
        WriteOptionalString(writer, "tool_version", matcher.ToolVersion);
        WriteOptionalString(writer, "canonical_path_pattern", matcher.CanonicalPathPattern);
        WriteOptionalString(writer, "resolved_executable", matcher.ResolvedExecutable);
        writer.WritePropertyName("argv_pattern");
        if (matcher.ArgvPattern is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var argument in matcher.ArgvPattern)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
        }

        WriteOptionalString(writer, "cwd_pattern", matcher.CwdPattern);
        WriteOptionalString(writer, "operation_class", matcher.OperationClass);
        WriteOptionalString(writer, "network_intent", matcher.NetworkIntent);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string ScopeName(PolicyRuleScope scope) => scope switch
    {
        PolicyRuleScope.Global => "global",
        PolicyRuleScope.Project => "project",
        PolicyRuleScope.Run => "run",
        PolicyRuleScope.Once => "once",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static string DecisionName(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => "allow",
        PolicyDecision.Ask => "ask",
        PolicyDecision.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };
}
