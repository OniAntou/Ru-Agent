using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Harness;

public static class BrokerPolicyContracts
{
    public const string StandardPresetWireValue = "standard";
    public const string FullAccessPresetWireValue = "full_access";

    public const string AllowWireValue = "allow";
    public const string AskWireValue = "ask";
    public const string DenyWireValue = "deny";

    public const string GlobalScopeWireValue = "global";
    public const string ProjectScopeWireValue = "project";
    public const string RunScopeWireValue = "run";
    public const string OnceScopeWireValue = "once";

    private static readonly HashSet<string> AllowedOperationClasses = new(StringComparer.Ordinal)
    {
        "read",
        "write",
        "edit",
        "delete",
        "list",
        "glob",
        "search",
        "patch",
        "restore",
        "discovery",
        "mutation",
        "terminal",
    };

    private static readonly HashSet<string> AllowedNetworkIntents = new(StringComparer.Ordinal)
    {
        "none",
        "declared",
        "egress",
        "unknown",
    };

    private static readonly string[] SnapshotKeys =
        ["version", "preset", "workspace", "ordered_rules", "created_at"];

    private static readonly string[] WorkspaceKeys =
        ["workspace_id", "volume_serial_number", "root_file_id", "display_identity_hash"];

    private static readonly string[] RuleKeys =
        ["rule_id", "scope", "decision", "matcher", "creation_order", "expires_at"];

    private static readonly string[] MatcherKeys =
        [
            "tool_id",
            "tool_version",
            "canonical_path_pattern",
            "resolved_executable",
            "argv_pattern",
            "cwd_pattern",
            "operation_class",
            "network_intent",
        ];

    public static IReadOnlyList<string> AuthoritativeFallbackCapabilities()
    {
        return new[]
        {
            "read", "write", "edit", "bash",
        };
    }

    public static bool IsReadOnlyTool(string toolId) =>
        toolId is "read";

    public static bool IsWebOp(string? op) =>
        string.Equals(op, "web_search", StringComparison.Ordinal)
        || string.Equals(op, "web_fetch", StringComparison.Ordinal);

    public static string CanonicalizePreset(string preset) => preset switch
    {
        "standard" => StandardPresetWireValue,
        "full_access" => FullAccessPresetWireValue,
        _ => throw new AgentProtocolException("invalid_preset"),
    };

    public static string CanonicalizeDecision(string decision) => decision switch
    {
        "allow" => AllowWireValue,
        "ask" => AskWireValue,
        "deny" => DenyWireValue,
        _ => throw new AgentProtocolException("invalid_decision"),
    };

    public static string CanonicalizeScope(string scope) => scope switch
    {
        "global" => GlobalScopeWireValue,
        "project" => ProjectScopeWireValue,
        "run" => RunScopeWireValue,
        "once" => OnceScopeWireValue,
        _ => throw new AgentProtocolException("invalid_scope"),
    };

    public static void ValidateSnapshotFields(long version, string presetWire, string sha256)
    {
        if (version <= 0)
        {
            throw new AgentProtocolException("invalid_snapshot_version");
        }

        _ = CanonicalizePreset(presetWire);

        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !IsLowerHex(sha256))
        {
            throw new AgentProtocolException("invalid_snapshot_hash");
        }
    }

    public static bool IsValidSha256(string value) =>
        value.Length == 64 && IsLowerHex(value);

    /// <summary>
    /// Computes the SHA-256 lowercase hex hash over the canonical UTF-8 encoding of the snapshot.
    /// Mirrors the Core canonicalizer layout byte-for-byte: version, preset, workspace,
    /// ordered_rules (rule_id, scope, decision, matcher, creation_order, expires_at), created_at.
    /// </summary>
    public static string ComputeCanonicalSnapshotHash(BrokerPolicySnapshotWire snapshot)
    {
        ValidateSnapshot(snapshot);
        return Convert.ToHexString(SHA256.HashData(CanonicalUtf8(snapshot))).ToLowerInvariant();
    }

    public static byte[] CanonicalUtf8(BrokerPolicySnapshotWire snapshot)
    {
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
            writer.WriteString("preset", snapshot.Preset);
            writer.WritePropertyName("workspace");
            writer.WriteStartObject();
            writer.WriteString("workspace_id", snapshot.Workspace.WorkspaceId);
            writer.WriteString("volume_serial_number", snapshot.Workspace.VolumeSerialNumber);
            writer.WriteString("root_file_id", snapshot.Workspace.RootFileId);
            writer.WriteString("display_identity_hash", snapshot.Workspace.DisplayIdentityHash);
            writer.WriteEndObject();

            writer.WritePropertyName("ordered_rules");
            writer.WriteStartArray();
            foreach (var rule in snapshot.OrderedRules)
            {
                writer.WriteStartObject();
                writer.WriteString("rule_id", rule.RuleId);
                writer.WriteString("scope", rule.Scope);
                writer.WriteString("decision", rule.Decision);
                writer.WritePropertyName("matcher");
                WriteMatcherCanonical(writer, rule.Matcher);
                writer.WriteNumber("creation_order", rule.CreationOrder);
                if (rule.ExpiresAt is null)
                {
                    writer.WriteNull("expires_at");
                }
                else
                {
                    writer.WriteString("expires_at", NormalizeTimestamp(rule.ExpiresAt));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteString("created_at", NormalizeTimestamp(snapshot.CreatedAt));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static JsonObject WriteSnapshot(BrokerPolicySnapshotWire snapshot)
    {
        ValidateSnapshot(snapshot);
        var rules = new JsonArray();
        foreach (var rule in snapshot.OrderedRules)
        {
            rules.Add(new JsonObject
            {
                ["rule_id"] = rule.RuleId,
                ["scope"] = rule.Scope,
                ["decision"] = rule.Decision,
                ["matcher"] = new JsonObject
                {
                    ["tool_id"] = rule.Matcher.ToolId,
                    ["tool_version"] = rule.Matcher.ToolVersion,
                    ["canonical_path_pattern"] = rule.Matcher.CanonicalPathPattern,
                    ["resolved_executable"] = rule.Matcher.ResolvedExecutable,
                    ["argv_pattern"] = rule.Matcher.ArgvPattern is null
                        ? null
                        : new JsonArray(rule.Matcher.ArgvPattern.Select(item => (JsonNode?)item).ToArray()),
                    ["cwd_pattern"] = rule.Matcher.CwdPattern,
                    ["operation_class"] = rule.Matcher.OperationClass,
                    ["network_intent"] = rule.Matcher.NetworkIntent,
                },
                ["creation_order"] = rule.CreationOrder,
                ["expires_at"] = rule.ExpiresAt,
            });
        }

        return new JsonObject
        {
            ["version"] = snapshot.Version,
            ["preset"] = snapshot.Preset,
            ["workspace"] = new JsonObject
            {
                ["workspace_id"] = snapshot.Workspace.WorkspaceId,
                ["volume_serial_number"] = snapshot.Workspace.VolumeSerialNumber,
                ["root_file_id"] = snapshot.Workspace.RootFileId,
                ["display_identity_hash"] = snapshot.Workspace.DisplayIdentityHash,
            },
            ["ordered_rules"] = rules,
            ["created_at"] = snapshot.CreatedAt,
        };
    }

    public static BrokerPolicySnapshotWire ReadSnapshot(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        RequireExactKeys(node, SnapshotKeys, "invalid_snapshot");

        if (node["workspace"] is not JsonObject workspaceNode)
        {
            throw new AgentProtocolException("invalid_snapshot");
        }

        RequireExactKeys(workspaceNode, WorkspaceKeys, "invalid_snapshot");

        if (node["ordered_rules"] is not JsonArray rulesNode)
        {
            throw new AgentProtocolException("invalid_snapshot");
        }

        var rules = new List<BrokerPolicyRuleWire>(rulesNode.Count);
        foreach (var ruleNode in rulesNode)
        {
            if (ruleNode is not JsonObject rule)
            {
                throw new AgentProtocolException("invalid_snapshot_rule");
            }

            RequireExactKeys(rule, RuleKeys, "invalid_snapshot_rule");
            if (rule["matcher"] is not JsonObject matcherNode)
            {
                throw new AgentProtocolException("invalid_snapshot_rule");
            }

            RequireExactKeys(matcherNode, MatcherKeys, "invalid_snapshot_rule");

            IReadOnlyList<string>? argvPattern = null;
            if (matcherNode["argv_pattern"] is not null)
            {
                if (matcherNode["argv_pattern"] is not JsonArray argvNode)
                {
                    throw new AgentProtocolException("invalid_snapshot_rule");
                }

                var argv = new List<string>(argvNode.Count);
                foreach (var item in argvNode)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var element))
                    {
                        throw new AgentProtocolException("invalid_snapshot_rule");
                    }

                    argv.Add(element);
                }

                argvPattern = argv;
            }

            rules.Add(new BrokerPolicyRuleWire(
                RuleId: RequiredString(rule, "rule_id", "invalid_snapshot_rule"),
                Scope: RequiredString(rule, "scope", "invalid_snapshot_rule"),
                Decision: RequiredString(rule, "decision", "invalid_snapshot_rule"),
                Matcher: new BrokerPolicyMatcherWire(
                    ToolId: OptionalString(matcherNode, "tool_id"),
                    ToolVersion: OptionalString(matcherNode, "tool_version"),
                    CanonicalPathPattern: OptionalString(matcherNode, "canonical_path_pattern"),
                    ResolvedExecutable: OptionalString(matcherNode, "resolved_executable"),
                    ArgvPattern: argvPattern,
                    CwdPattern: OptionalString(matcherNode, "cwd_pattern"),
                    OperationClass: OptionalString(matcherNode, "operation_class"),
                    NetworkIntent: OptionalString(matcherNode, "network_intent")),
                CreationOrder: RequiredLong(rule, "creation_order", "invalid_snapshot_rule"),
                ExpiresAt: OptionalString(rule, "expires_at")));
        }

        return new BrokerPolicySnapshotWire(
            Version: RequiredLong(node, "version", "invalid_snapshot_version"),
            Preset: RequiredString(node, "preset", "invalid_preset"),
            Workspace: new BrokerPolicyWorkspaceIdentityWire(
                WorkspaceId: RequiredString(workspaceNode, "workspace_id", "invalid_workspace_id"),
                VolumeSerialNumber: RequiredString(workspaceNode, "volume_serial_number", "invalid_volume_serial"),
                RootFileId: RequiredString(workspaceNode, "root_file_id", "invalid_root_file_id"),
                DisplayIdentityHash: RequiredString(workspaceNode, "display_identity_hash", "invalid_display_hash")),
            OrderedRules: rules,
            CreatedAt: RequiredString(node, "created_at", "invalid_snapshot"));
    }

    private static void WriteMatcherCanonical(Utf8JsonWriter writer, BrokerPolicyMatcherWire matcher)
    {
        writer.WriteStartObject();
        WriteOptionalCanonical(writer, "tool_id", matcher.ToolId);
        WriteOptionalCanonical(writer, "tool_version", matcher.ToolVersion);
        WriteOptionalCanonical(writer, "canonical_path_pattern", matcher.CanonicalPathPattern);
        WriteOptionalCanonical(writer, "resolved_executable", matcher.ResolvedExecutable);
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

        WriteOptionalCanonical(writer, "cwd_pattern", matcher.CwdPattern);
        WriteOptionalCanonical(writer, "operation_class", matcher.OperationClass);
        WriteOptionalCanonical(writer, "network_intent", matcher.NetworkIntent);
        writer.WriteEndObject();
    }

    private static void WriteOptionalCanonical(Utf8JsonWriter writer, string propertyName, string? value)
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

    private static string NormalizeTimestamp(string wireTimestamp)
    {
        if (!TryParseTimestamp(wireTimestamp, out var parsed))
        {
            throw new AgentProtocolException("invalid_snapshot");
        }

        return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);

    private static void RequireExactKeys(JsonObject node, IReadOnlyList<string> expectedKeys, string code)
    {
        foreach (var key in node)
        {
            if (!expectedKeys.Contains(key.Key, StringComparer.Ordinal))
            {
                throw new AgentProtocolException(code);
            }
        }

        foreach (var key in expectedKeys)
        {
            if (!node.ContainsKey(key))
            {
                throw new AgentProtocolException(code);
            }
        }
    }

    private static string RequiredString(JsonObject node, string property, string code) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw new AgentProtocolException(code);

    private static string? OptionalString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static long RequiredLong(JsonObject node, string property, string code)
    {
        if (node[property] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longResult))
            {
                return longResult;
            }

            if (value.TryGetValue<int>(out var intResult))
            {
                return intResult;
            }
        }

        throw new AgentProtocolException(code);
    }

    private static void RequireBounded(string? value, int maximumLength)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }
    }

    public static void ValidateSnapshot(BrokerPolicySnapshotWire snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version <= 0)
        {
            throw new AgentProtocolException("invalid_snapshot_version");
        }

        _ = CanonicalizePreset(snapshot.Preset);
        ValidateWorkspaceIdentity(snapshot.Workspace);
        ArgumentNullException.ThrowIfNull(snapshot.OrderedRules);
        if (!TryParseTimestamp(snapshot.CreatedAt, out _))
        {
            throw new AgentProtocolException("invalid_snapshot");
        }

        foreach (var rule in snapshot.OrderedRules)
        {
            ValidateRule(rule);
        }
    }

    public static void ValidateWorkspaceIdentity(BrokerPolicyWorkspaceIdentityWire identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.WorkspaceId) || identity.WorkspaceId.Length > 128)
        {
            throw new AgentProtocolException("invalid_workspace_id");
        }

        if (string.IsNullOrWhiteSpace(identity.VolumeSerialNumber) || identity.VolumeSerialNumber.Length > 64)
        {
            throw new AgentProtocolException("invalid_volume_serial");
        }

        if (string.IsNullOrWhiteSpace(identity.RootFileId) || identity.RootFileId.Length > 128)
        {
            throw new AgentProtocolException("invalid_root_file_id");
        }

        if (string.IsNullOrWhiteSpace(identity.DisplayIdentityHash) || !IsValidSha256(identity.DisplayIdentityHash))
        {
            throw new AgentProtocolException("invalid_display_hash");
        }
    }

    public static void ValidateRule(BrokerPolicyRuleWire rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.RuleId) || rule.RuleId.Length > 128)
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        _ = CanonicalizeScope(rule.Scope);
        _ = CanonicalizeDecision(rule.Decision);
        if (rule.CreationOrder < 0)
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        if (rule.ExpiresAt is not null && !TryParseTimestamp(rule.ExpiresAt, out _))
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        ValidateMatcher(rule.Matcher);
    }

    public static void ValidateMatcher(BrokerPolicyMatcherWire matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        RequireBounded(matcher.ToolId, 128);
        RequireBounded(matcher.ToolVersion, 32);
        RequireBounded(matcher.CanonicalPathPattern, 4096);
        RequireBounded(matcher.ResolvedExecutable, 256);
        RequireBounded(matcher.CwdPattern, 4096);

        if (matcher.OperationClass is not null && !AllowedOperationClasses.Contains(matcher.OperationClass))
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        if (matcher.NetworkIntent is not null && !AllowedNetworkIntents.Contains(matcher.NetworkIntent))
        {
            throw new AgentProtocolException("invalid_snapshot_rule");
        }

        if (matcher.ArgvPattern is not null)
        {
            if (matcher.ArgvPattern.Count > 64)
            {
                throw new AgentProtocolException("invalid_snapshot_rule");
            }

            foreach (var element in matcher.ArgvPattern)
            {
                RequireBounded(element, 4096);
            }
        }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record BrokerPolicyWorkspaceIdentityWire(
    string WorkspaceId,
    string VolumeSerialNumber,
    string RootFileId,
    string DisplayIdentityHash);

public sealed record BrokerPolicyMatcherWire(
    string? ToolId,
    string? ToolVersion,
    string? CanonicalPathPattern,
    string? ResolvedExecutable,
    IReadOnlyList<string>? ArgvPattern,
    string? CwdPattern,
    string? OperationClass,
    string? NetworkIntent);

public sealed record BrokerPolicyRuleWire(
    string RuleId,
    string Scope,
    string Decision,
    BrokerPolicyMatcherWire Matcher,
    long CreationOrder,
    string? ExpiresAt);

public sealed record BrokerPolicySnapshotWire(
    long Version,
    string Preset,
    BrokerPolicyWorkspaceIdentityWire Workspace,
    IReadOnlyList<BrokerPolicyRuleWire> OrderedRules,
    string CreatedAt);
