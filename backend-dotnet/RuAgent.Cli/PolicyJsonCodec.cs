using System.Text;
using System.Text.Json;

namespace RuAgent.Cli;

public sealed record PolicyFileRule(
    string Scope,
    string Decision,
    PolicyFileMatcher Matcher);

public sealed record PolicyFileMatcher(
    string? ToolId,
    string? ToolVersion,
    string? CanonicalPathPattern,
    string? ResolvedExecutable,
    IReadOnlyList<string>? ArgvPattern,
    string? CwdPattern,
    string? OperationClass,
    string? NetworkIntent);

public sealed record PolicyFile(
    int Version,
    IReadOnlyList<PolicyFileRule> Rules);

public sealed class PolicyJsonException : Exception
{
    public string ErrorCode { get; }
    public PolicyJsonException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
}

public static class PolicyJsonCodec
{
    private static readonly HashSet<string> AllowedTopFields = new(StringComparer.Ordinal) { "version", "rules" };
    private static readonly HashSet<string> AllowedRuleFields = new(StringComparer.Ordinal) { "scope", "decision", "matcher" };
    private static readonly HashSet<string> AllowedMatcherFields = new(StringComparer.Ordinal)
    {
        "tool_id", "tool_version", "canonical_path_pattern", "resolved_executable", "argv_pattern", "cwd_pattern", "operation_class", "network_intent"
    };
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.Ordinal) { "global", "project", "run", "once" };
    private static readonly HashSet<string> AllowedDecisions = new(StringComparer.Ordinal) { "allow", "ask", "deny" };

    public static PolicyFile Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PolicyJsonException("invalid_policy_json", "Policy JSON is empty.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PolicyJsonException("invalid_policy_json", $"Invalid JSON: {ex.Message}");
        }
        using var documentLease = doc;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new PolicyJsonException("invalid_policy_json", "Root must be object.");

        foreach (var prop in root.EnumerateObject())
        {
            if (!AllowedTopFields.Contains(prop.Name))
                throw new PolicyJsonException("unknown_field", $"Unknown field '{prop.Name}'.");
        }

        if (!root.TryGetProperty("version", out var versionProp))
            throw new PolicyJsonException("invalid_policy_json", "Missing version.");

        if (versionProp.ValueKind != JsonValueKind.Number || !versionProp.TryGetInt32(out var version))
            throw new PolicyJsonException("invalid_policy_json", "version must be number.");

        if (version != 1)
            throw new PolicyJsonException("unknown_version", $"Unknown policy version {version}.");

        if (!root.TryGetProperty("rules", out var rulesProp) || rulesProp.ValueKind != JsonValueKind.Array)
            throw new PolicyJsonException("invalid_policy_json", "Missing rules array.");

        var rules = new List<PolicyFileRule>();
        int index = 0;
        foreach (var ruleEl in rulesProp.EnumerateArray())
        {
            if (ruleEl.ValueKind != JsonValueKind.Object)
                throw new PolicyJsonException("invalid_policy_json", $"Rule {index} must be object.");

            foreach (var prop in ruleEl.EnumerateObject())
            {
                if (!AllowedRuleFields.Contains(prop.Name))
                    throw new PolicyJsonException("unknown_field", $"Unknown rule field '{prop.Name}'.");
            }

            if (!ruleEl.TryGetProperty("scope", out var scopeProp) || scopeProp.ValueKind != JsonValueKind.String)
                throw new PolicyJsonException("invalid_policy_json", $"Rule {index} missing scope.");

            var scope = scopeProp.GetString()!.Trim().ToLowerInvariant();
            if (!AllowedScopes.Contains(scope))
                throw new PolicyJsonException("invalid_scope", $"Rule {index} invalid scope '{scope}'.");

            if (!ruleEl.TryGetProperty("decision", out var decProp) || decProp.ValueKind != JsonValueKind.String)
                throw new PolicyJsonException("invalid_policy_json", $"Rule {index} missing decision.");

            var decision = decProp.GetString()!.Trim().ToLowerInvariant();
            if (!AllowedDecisions.Contains(decision))
                throw new PolicyJsonException("invalid_decision", $"Rule {index} invalid decision '{decision}'.");

            if (!ruleEl.TryGetProperty("matcher", out var matcherProp) || matcherProp.ValueKind != JsonValueKind.Object)
                throw new PolicyJsonException("invalid_policy_json", $"Rule {index} missing matcher.");

            var matcher = ParseMatcher(matcherProp, index);
            rules.Add(new PolicyFileRule(scope, decision, matcher));
            index++;
            if (index > 64)
                throw new PolicyJsonException("too_many_rules", "Too many rules (max 64).");
        }

        return new PolicyFile(version, rules);
    }

    private static PolicyFileMatcher ParseMatcher(JsonElement matcherProp, int ruleIndex)
    {
        foreach (var prop in matcherProp.EnumerateObject())
        {
            if (!AllowedMatcherFields.Contains(prop.Name))
                throw new PolicyJsonException("unknown_field", $"Rule {ruleIndex} unknown matcher field '{prop.Name}'.");
        }

        string? toolId = null;
        if (matcherProp.TryGetProperty("tool_id", out var tid) && tid.ValueKind == JsonValueKind.String) toolId = tid.GetString();
        else if (matcherProp.TryGetProperty("tool_id", out var tid2) && tid2.ValueKind != JsonValueKind.Null && tid2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} tool_id must be string.");

        string? toolVersion = null;
        if (matcherProp.TryGetProperty("tool_version", out var tv) && tv.ValueKind == JsonValueKind.String) toolVersion = tv.GetString();
        else if (matcherProp.TryGetProperty("tool_version", out var tv2) && tv2.ValueKind != JsonValueKind.Null && tv2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} tool_version must be string.");

        string? pathPattern = null;
        if (matcherProp.TryGetProperty("canonical_path_pattern", out var pp) && pp.ValueKind == JsonValueKind.String) pathPattern = pp.GetString();
        else if (matcherProp.TryGetProperty("canonical_path_pattern", out var pp2) && pp2.ValueKind != JsonValueKind.Null && pp2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} canonical_path_pattern must be string.");

        string? resolvedExe = null;
        if (matcherProp.TryGetProperty("resolved_executable", out var re) && re.ValueKind == JsonValueKind.String) resolvedExe = re.GetString();
        else if (matcherProp.TryGetProperty("resolved_executable", out var re2) && re2.ValueKind != JsonValueKind.Null && re2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} resolved_executable must be string.");

        IReadOnlyList<string>? argvPattern = null;
        if (matcherProp.TryGetProperty("argv_pattern", out var argvProp))
        {
            if (argvProp.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in argvProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} argv_pattern must be string array.");
                    var s = el.GetString()!;
                    if (s.Length > 4096) throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} argv_pattern entry too long.");
                    list.Add(s);
                }

                argvPattern = list;
            }
            else if (argvProp.ValueKind != JsonValueKind.Null)
            {
                throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} argv_pattern must be array or null.");
            }
        }

        string? cwdPattern = null;
        if (matcherProp.TryGetProperty("cwd_pattern", out var cwd) && cwd.ValueKind == JsonValueKind.String) cwdPattern = cwd.GetString();
        else if (matcherProp.TryGetProperty("cwd_pattern", out var cwd2) && cwd2.ValueKind != JsonValueKind.Null && cwd2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} cwd_pattern must be string.");

        string? opClass = null;
        if (matcherProp.TryGetProperty("operation_class", out var oc) && oc.ValueKind == JsonValueKind.String) opClass = oc.GetString();
        else if (matcherProp.TryGetProperty("operation_class", out var oc2) && oc2.ValueKind != JsonValueKind.Null && oc2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} operation_class must be string.");

        string? netIntent = null;
        if (matcherProp.TryGetProperty("network_intent", out var ni) && ni.ValueKind == JsonValueKind.String) netIntent = ni.GetString();
        else if (matcherProp.TryGetProperty("network_intent", out var ni2) && ni2.ValueKind != JsonValueKind.Null && ni2.ValueKind != JsonValueKind.String)
            throw new PolicyJsonException("invalid_matcher", $"Rule {ruleIndex} network_intent must be string.");

        // Bounded lengths
        if (toolId is not null && toolId.Length > 256) throw new PolicyJsonException("invalid_matcher", "tool_id too long.");
        if (pathPattern is not null && pathPattern.Length > 4096) throw new PolicyJsonException("invalid_matcher", "path pattern too long.");
        if (cwdPattern is not null && cwdPattern.Length > 4096) throw new PolicyJsonException("invalid_matcher", "cwd pattern too long.");

        return new PolicyFileMatcher(toolId, toolVersion, pathPattern, resolvedExe, argvPattern, cwdPattern, opClass, netIntent);
    }

    public static string NormalizeAndDisplay(PolicyFile file)
    {
        // Produce normalized JSON with sorted keys for display before confirmation
        // Use UTF-8 writer with sorted keys; for display we sort matcher keys alphabetically
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", file.Version);
            writer.WritePropertyName("rules");
            writer.WriteStartArray();
            foreach (var rule in file.Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("scope", rule.Scope);
                writer.WriteString("decision", rule.Decision);
                writer.WritePropertyName("matcher");
                writer.WriteStartObject();
                // Sorted matcher keys
                var matcherDict = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                if (rule.Matcher.ToolId is not null) matcherDict["tool_id"] = rule.Matcher.ToolId;
                if (rule.Matcher.ToolVersion is not null) matcherDict["tool_version"] = rule.Matcher.ToolVersion;
                if (rule.Matcher.CanonicalPathPattern is not null) matcherDict["canonical_path_pattern"] = rule.Matcher.CanonicalPathPattern;
                if (rule.Matcher.ResolvedExecutable is not null) matcherDict["resolved_executable"] = rule.Matcher.ResolvedExecutable;
                if (rule.Matcher.ArgvPattern is not null) matcherDict["argv_pattern"] = rule.Matcher.ArgvPattern;
                if (rule.Matcher.CwdPattern is not null) matcherDict["cwd_pattern"] = rule.Matcher.CwdPattern;
                if (rule.Matcher.OperationClass is not null) matcherDict["operation_class"] = rule.Matcher.OperationClass;
                if (rule.Matcher.NetworkIntent is not null) matcherDict["network_intent"] = rule.Matcher.NetworkIntent;

                foreach (var kv in matcherDict)
                {
                    if (kv.Value is IReadOnlyList<string> list)
                    {
                        writer.WritePropertyName(kv.Key);
                        writer.WriteStartArray();
                        foreach (var s in list) writer.WriteStringValue(s);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString(kv.Key, (string?)kv.Value);
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static PolicyFile LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new PolicyJsonException("invalid_path", "Path is empty.");
        if (!File.Exists(path)) throw new PolicyJsonException("file_not_found", $"File not found: {path}");
        const int maximumBytes = 1 * 1024 * 1024;
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) throw new PolicyJsonException("file_too_large", "Policy file too large.");
        var bytes = ReadBoundedBytes(path, maximumBytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        return Parse(json);
    }

    private static byte[] ReadBoundedBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new PolicyJsonException("file_too_large", "Policy file too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
