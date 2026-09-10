using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public sealed class AgentToolDefinition
{
    private readonly Func<JsonObject> _createInputSchema;
    private readonly Func<JsonObject>? _createExampleArguments;

    internal AgentToolDefinition(int ordinal, string toolId, string toolVersion, string description, string scope, Func<JsonObject> createInputSchema, Func<JsonObject>? createExampleArguments)
    {
        Ordinal = ordinal;
        ToolId = toolId;
        ToolVersion = toolVersion;
        Description = description;
        Scope = scope;
        _createInputSchema = createInputSchema;
        _createExampleArguments = createExampleArguments;
    }

    internal int Ordinal { get; }
    public string ToolId { get; }
    public string ToolVersion { get; }
    public string Description { get; }
    public string Scope { get; }
    public JsonObject InputSchema => _createInputSchema();
    public JsonObject? ExampleArguments => _createExampleArguments?.Invoke();
}

public static class AgentToolDefinitionCatalog
{
    public const string CurrentVersion = "1.0.0";
    private const string FilePathGuidance = " Use a workspace-relative path, or preserve an exact absolute path supplied by the user inside a directory granted for this session.";

    private sealed record Entry(string Id, string Description, string Scope, Func<JsonObject> Schema, Func<JsonObject>? Example = null);

    private static readonly IReadOnlyList<Entry> Entries =
    [
        new("read", "Read-only workspace listing, file view, and bundled docs. Set op to list, read, or help." + FilePathGuidance, AgentToolScopes.WorkspaceRead, ReadSchema, () => new JsonObject { ["op"] = "read", ["path"] = "README.md" }),
        new("grep", "Search workspace file contents. Ripgrep-based text search with regex, glob filter, and bounded results." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GrepSchema, () => new JsonObject { ["query"] = "TODO", ["path"] = "." }),
        new("glob", "Find workspace files by glob pattern. Bounded ordinal results with traversal caps." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GlobSchema, () => new JsonObject { ["pattern"] = "**/*.cs", ["path"] = "." }),
        new("write", "Write, delete, or restore a file; policy still controls mutation and overwrite." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, WriteSchema, () => new JsonObject { ["op"] = "write", ["path"] = "notes.txt", ["content"] = "new content" }),
        new("edit", "Replace an exact non-empty string in a file." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, FileEditSchema, () => new JsonObject { ["path"] = "notes.txt", ["old_string"] = "old", ["new_string"] = "new" }),
        new("bash", "Run one bounded executable or shell script with an argument vector and explicit network intent.", AgentToolScopes.TerminalExecute, BashSchema, () => new JsonObject { ["mode"] = "run", ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = "." }),
        new("web_search", "Search the public web. Google HTML is the primary engine with DuckDuckGo HTML fallback. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkSearch, WebSearchSchema, () => new JsonObject { ["query"] = "latest dotnet release" }),
        new("web_fetch", "Fetch one public web page as sanitized text. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkFetch, WebFetchSchema, () => new JsonObject { ["url"] = "https://example.com" }),
    ];

    private static readonly IReadOnlyList<AgentToolDefinition> Definitions = BuildDefinitions();
    private static readonly IReadOnlyDictionary<string, AgentToolDefinition> ByKey = Definitions.ToDictionary(item => Key(item.ToolId, item.ToolVersion), StringComparer.Ordinal);

    public static IReadOnlyList<AgentToolDefinition> Get() => Definitions;
    public static AgentToolDefinition? Find(string toolId, string toolVersion) => toolId is not null && toolVersion is not null && ByKey.TryGetValue(Key(toolId, toolVersion), out var definition) ? definition : null;

    public static string ComputeSchemaDigest(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var canonical = schema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static IReadOnlyList<AgentToolDefinition> BuildDefinitions()
    {
        var result = new List<AgentToolDefinition>(Entries.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            if (!keys.Add(Key(entry.Id, CurrentVersion))) throw new AgentProtocolException("duplicate_tool_definition");
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Id, "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$") || entry.Description.Length is 0 or > 2048 || !AgentToolScopes.All.Contains(entry.Scope)) throw new AgentProtocolException("invalid_tool_definition");
            var schema = entry.Schema();
            if (!IsStrictObjectSchema(schema)) throw new AgentProtocolException("invalid_tool_definition");
            var example = entry.Example?.Invoke();
            if (example is not null && !ExampleMatches(schema, example)) throw new AgentProtocolException("invalid_tool_definition");
            result.Add(new AgentToolDefinition(index, entry.Id, CurrentVersion, entry.Description, entry.Scope, entry.Schema, entry.Example));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static string Key(string id, string version) => $"{id}\u001f{version}";
    private static bool IsStrictObjectSchema(JsonObject schema) => schema["type"]?.GetValue<string>() == "object" && schema["additionalProperties"]?.GetValue<bool>() == false && schema["properties"] is JsonObject;

    private static bool ExampleMatches(JsonObject schema, JsonObject example)
    {
        var properties = schema["properties"]!.AsObject();
        if (example.Any(item => !properties.ContainsKey(item.Key))) return false;
        if (schema["required"] is JsonArray required && required.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out var field) || !example.ContainsKey(field))) return false;
        foreach (var item in example)
        {
            if (item.Value is null || properties[item.Key] is not JsonObject property) return false;
            var type = property["type"]?.GetValue<string>();
            if (type == "string" && item.Value.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
            if (type == "boolean" && item.Value.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return false;
            if (type == "integer" && (item.Value is not JsonValue integerValue || !integerValue.TryGetValue<int>(out _) || item.Value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return false;
            if (type == "array" && item.Value is not JsonArray) return false;
        }
        return true;
    }

    private static JsonObject ReadSchema() => ObjectSchema(["op"], ReadSchemaProperties());
    private static JsonObject WriteSchema() => ObjectSchema(["op"], WriteSchemaProperties());
    private static JsonObject BashSchema() => ObjectSchema(["mode"], BashSchemaProperties());
    private static (string Name, JsonObject Schema)[] ReadSchemaProperties() =>
    [
        ("op", EnumSchema(["list", "read", "help"])),
        ("path", StringSchema()),
        ("offset", IntegerSchema(0)), ("limit", IntegerSchema(1, 256 * 1024)),
        ("max_entries", IntegerSchema(1, 512)),
        ("query", StringSchema(minLength: 1, maxLength: 256)), ("max_results", IntegerSchema(1, 5)),
    ];
    private static JsonObject GrepSchema() => ObjectSchema(["query"], GrepSchemaProperties());
    private static (string Name, JsonObject Schema)[] GrepSchemaProperties() =>
    [
        ("query", StringSchema(512)),
        ("path", StringSchema()), ("file_glob", StringSchema()),
        ("case_sensitive", BooleanSchema()), ("is_regex", BooleanSchema()),
        ("max_results", IntegerSchema(1, 64)),
    ];
    private static JsonObject GlobSchema() => ObjectSchema(["pattern"], GlobSchemaProperties());
    private static (string Name, JsonObject Schema)[] GlobSchemaProperties() =>
    [
        ("pattern", StringSchema()),
        ("path", StringSchema()), ("max_results", IntegerSchema(1, 64)),
    ];
    private static (string Name, JsonObject Schema)[] WriteSchemaProperties() =>
    [
        ("op", EnumSchema(["write", "delete", "restore"])),
        ("path", StringSchema()), ("content", StringSchema()), ("overwrite", BooleanSchema()),
        ("recovery_id", StringSchema(maxLength: 256)),
    ];
    private static (string Name, JsonObject Schema)[] BashSchemaProperties() =>
    [
        ("mode", EnumSchema(["run", "shell"])),
        ("executable", StringSchema(TerminalLimits.MaxExecutableChars)),
        ("arguments", ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars)),
        ("script", StringSchema(TerminalLimits.MaxScriptChars)),
        ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)),
        ("timeout_ms", IntegerSchema(1, TerminalLimits.HardMaxTimeoutMs)),
        ("network_intent", BooleanSchema()),
        ("read_only", BooleanSchema()),
        ("write_set", ArraySchema(256, TerminalLimits.MaxWorkingDirectoryChars)),
    ];
    private static JsonObject FileListSchema() => ObjectSchema(properties: [("path", StringSchema()), ("max_entries", IntegerSchema(1, 512))]);
    private static JsonObject FileGlobSchema() => ObjectSchema(["pattern"], [("path", StringSchema()), ("pattern", StringSchema()), ("max_results", IntegerSchema(1, 64))]);
    private static JsonObject FileSearchSchema() => ObjectSchema(["query"], [("path", StringSchema()), ("query", StringSchema()), ("file_glob", StringSchema()), ("case_sensitive", BooleanSchema()), ("is_regex", BooleanSchema()), ("max_results", IntegerSchema(1, 64))]);
    private static JsonObject FileReadSchema() => ObjectSchema(["path"], [("path", StringSchema()), ("offset", IntegerSchema(0)), ("limit", IntegerSchema(1, 256 * 1024))]);
    private static JsonObject FileWriteSchema() => ObjectSchema(["path", "content"], [("path", StringSchema()), ("content", StringSchema()), ("overwrite", BooleanSchema())]);
    private static JsonObject FileEditSchema() => ObjectSchema(["path", "old_string"], [("path", StringSchema()), ("old_string", StringSchema(minLength: 1)), ("new_string", StringSchema()), ("expected_occurrences", IntegerSchema(1, 1024))]);
    private static JsonObject FileDeleteSchema() => ObjectSchema(["path"], [("path", StringSchema())]);
    private static JsonObject FileRestoreSchema() => ObjectSchema(["recovery_id"], [("recovery_id", StringSchema(maxLength: 256))]);
    private static JsonObject HelpDocsSchema() => ObjectSchema(["query"], [("query", StringSchema(minLength: 1, maxLength: 256)), ("max_results", IntegerSchema(1, 5))]);
    private static JsonObject TerminalRunSchema() => ObjectSchema(["executable"], [("executable", StringSchema(TerminalLimits.MaxExecutableChars)), ("arguments", ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars)), ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)), ("timeout_ms", IntegerSchema(1, TerminalLimits.HardMaxTimeoutMs)), ("network_intent", BooleanSchema()), ..TerminalWriteSetProperties()]);
    private static JsonObject TerminalShellSchema() => ObjectSchema(["script"], [("script", StringSchema(TerminalLimits.MaxScriptChars)), ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)), ("timeout_ms", IntegerSchema(1, TerminalLimits.HardMaxTimeoutMs)), ("network_intent", BooleanSchema()), ..TerminalWriteSetProperties()]);
    private static JsonObject WebSearchSchema() => ObjectSchema(["query"], [("query", StringSchema(512)), ("max_results", IntegerSchema(1, 10))]);
    private static JsonObject WebFetchSchema() => ObjectSchema(["url"], [("url", StringSchema(2048))]);

    private static JsonObject ObjectSchema(string[]? required = null, params (string Name, JsonObject Schema)[] properties)
    {
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject(properties.Select(item => KeyValuePair.Create<string, JsonNode?>(item.Name, item.Schema))) };
        if (required is { Length: > 0 }) schema["required"] = new JsonArray(required.Select(item => (JsonNode?)item).ToArray());
        return schema;
    }

    private static JsonObject StringSchema(int? maxLength = null, int? minLength = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (maxLength is not null) schema["maxLength"] = maxLength.Value;
        if (minLength is not null) schema["minLength"] = minLength.Value;
        return schema;
    }

    private static JsonObject ArraySchema(int maxItems, int itemMaxLength) => new() { ["type"] = "array", ["items"] = StringSchema(itemMaxLength), ["maxItems"] = maxItems };
    private static JsonObject EnumSchema(params string[] choices) => new() { ["type"] = "string", ["enum"] = new JsonArray(choices.Select(item => (JsonNode?)item).ToArray()) };
    private static (string Name, JsonObject Schema)[] TerminalWriteSetProperties() => [("read_only", BooleanSchema()), ("write_set", ArraySchema(256, TerminalLimits.MaxWorkingDirectoryChars))];
    private static JsonObject BooleanSchema() => new() { ["type"] = "boolean" };
    private static JsonObject IntegerSchema(int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum is not null) schema["minimum"] = minimum.Value;
        if (maximum is not null) schema["maximum"] = maximum.Value;
        return schema;
    }
}
