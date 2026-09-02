using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

[Flags]
public enum AgentToolExposure
{
    None = 0,
    ModelVisible = 1,
    BrokerInternal = 2,
    DeterministicHost = 4,
}

public sealed class AgentToolDefinition
{
    private readonly Func<JsonObject> _createInputSchema;
    private readonly Func<JsonObject>? _createExampleArguments;

    internal AgentToolDefinition(int ordinal, string toolId, string toolVersion, string description, string scope, AgentToolExposure exposure, Func<JsonObject> createInputSchema, Func<JsonObject>? createExampleArguments)
    {
        Ordinal = ordinal;
        ToolId = toolId;
        ToolVersion = toolVersion;
        Description = description;
        Scope = scope;
        Exposure = exposure;
        _createInputSchema = createInputSchema;
        _createExampleArguments = createExampleArguments;
    }

    internal int Ordinal { get; }
    public string ToolId { get; }
    public string ToolVersion { get; }
    public string Description { get; }
    public string Scope { get; }
    public AgentToolExposure Exposure { get; }
    public JsonObject InputSchema => _createInputSchema();
    public JsonObject? ExampleArguments => _createExampleArguments?.Invoke();
}

public static class AgentToolDefinitionCatalog
{
    public const string CurrentVersion = "1.0.0";

    private sealed record Entry(string Id, string Description, string Scope, AgentToolExposure Exposure, Func<JsonObject> Schema, Func<JsonObject>? Example = null);

    private static readonly IReadOnlyList<Entry> Entries =
    [
        new("files.list", "List entries in the trusted workspace; keep the path workspace-relative.", AgentToolScopes.WorkspaceRead, AgentToolExposure.ModelVisible, FileListSchema, () => new JsonObject { ["path"] = "workspace", ["max_entries"] = 32 }),
        new("files.glob", "Find workspace files by a bounded glob pattern.", AgentToolScopes.WorkspaceRead, AgentToolExposure.ModelVisible, FileGlobSchema, () => new JsonObject { ["path"] = "workspace", ["pattern"] = "*.txt" }),
        new("files.search", "Search workspace file contents; set is_regex only when the query is a valid regular expression.", AgentToolScopes.WorkspaceRead, AgentToolExposure.ModelVisible, FileSearchSchema, () => new JsonObject { ["query"] = "search text", ["is_regex"] = false }),
        new("files.read", "Read bounded UTF-8 content from a workspace-relative file.", AgentToolScopes.WorkspaceRead, AgentToolExposure.ModelVisible, FileReadSchema, () => new JsonObject { ["path"] = "README.md", ["offset"] = 0, ["limit"] = 1024 }),
        new("files.write", "Write workspace content; policy still controls mutation and overwrite.", AgentToolScopes.WorkspaceWrite, AgentToolExposure.ModelVisible, FileWriteSchema, () => new JsonObject { ["path"] = "notes.txt", ["content"] = "new content" }),
        new("files.edit", "Replace an exact non-empty string in a workspace file.", AgentToolScopes.WorkspaceWrite, AgentToolExposure.ModelVisible, FileEditSchema, () => new JsonObject { ["path"] = "notes.txt", ["old_string"] = "old", ["new_string"] = "new" }),
        new("files.delete", "Delete one workspace-relative file after policy and approval checks.", AgentToolScopes.WorkspaceWrite, AgentToolExposure.ModelVisible, FileDeleteSchema, () => new JsonObject { ["path"] = "obsolete.txt" }),
        new("files.restore", "Restore a previously prepared file mutation by recovery ID.", AgentToolScopes.WorkspaceWrite, AgentToolExposure.ModelVisible, FileRestoreSchema, () => new JsonObject { ["recovery_id"] = "recovery-123" }),
        new("terminal.run", "Run one bounded executable with an argument vector and explicit network intent.", AgentToolScopes.TerminalExecute, AgentToolExposure.ModelVisible, TerminalRunSchema, () => new JsonObject { ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = ".", ["network_intent"] = false }),
        new("terminal.shell", "Run bounded shell script text after terminal policy and approval checks.", AgentToolScopes.TerminalExecute, AgentToolExposure.ModelVisible, TerminalShellSchema, () => new JsonObject { ["script"] = "Get-Date", ["working_directory"] = ".", ["network_intent"] = false }),
        new("web.search", "Search the web with one bounded query when managed web search is enabled.", AgentToolScopes.NetworkSearch, AgentToolExposure.ModelVisible, WebSearchSchema, () => new JsonObject { ["query"] = "latest Ru release" }),
        new("web.fetch", "Fetch one absolute HTTP(S) URL when managed web fetch is enabled.", AgentToolScopes.NetworkFetch, AgentToolExposure.ModelVisible, WebFetchSchema, () => new JsonObject { ["url"] = "https://example.com/" }),
        new("help.docs", "Search bundled Ru documentation for product behavior, tools, configuration, and operating boundaries.", AgentToolScopes.SystemRead, AgentToolExposure.ModelVisible, HelpDocsSchema, () => new JsonObject { ["query"] = "how approvals work", ["max_results"] = 3 }),
        new("system.time", "Read the host clock for deterministic system operations.", AgentToolScopes.SystemRead, AgentToolExposure.BrokerInternal, EmptySchema),
        new("system.status", "Read host runtime status for internal Broker operations.", AgentToolScopes.SystemRead, AgentToolExposure.BrokerInternal, EmptySchema),
        new("app.list", "List locally registered applications for internal Broker operations.", AgentToolScopes.SystemRead, AgentToolExposure.BrokerInternal, EmptySchema),
        new("notes.create", "Create a durable note through the internal Broker path.", AgentToolScopes.WorkspaceWrite, AgentToolExposure.BrokerInternal, NotesCreateSchema, () => new JsonObject { ["path"] = "note.md", ["title"] = "Title", ["content"] = "Content" }),
        new("system.volume", "Perform a deterministic host volume intent; never select this as an ordinary model tool.", AgentToolScopes.SystemWrite, AgentToolExposure.BrokerInternal | AgentToolExposure.DeterministicHost, VolumeSchema, () => new JsonObject { ["operation"] = "set", ["level"] = 30 }),
    ];

    private static readonly IReadOnlyList<AgentToolDefinition> Definitions = BuildDefinitions();
    private static readonly IReadOnlyDictionary<string, AgentToolDefinition> ByKey = Definitions.ToDictionary(item => Key(item.ToolId, item.ToolVersion), StringComparer.Ordinal);

    public static IReadOnlyList<AgentToolDefinition> Get() => Definitions;
    public static IReadOnlyList<AgentToolDefinition> Select(AgentToolExposure exposure) => Definitions.Where(item => (item.Exposure & exposure) == exposure).ToArray();
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
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Id, "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)+$") || entry.Description.Length is 0 or > 2048 || !AgentToolScopes.All.Contains(entry.Scope) || entry.Exposure == AgentToolExposure.None) throw new AgentProtocolException("invalid_tool_definition");
            var schema = entry.Schema();
            if (!IsStrictObjectSchema(schema)) throw new AgentProtocolException("invalid_tool_definition");
            var example = entry.Example?.Invoke();
            if (example is not null && !ExampleMatches(schema, example)) throw new AgentProtocolException("invalid_tool_definition");
            result.Add(new AgentToolDefinition(index, entry.Id, CurrentVersion, entry.Description, entry.Scope, entry.Exposure, entry.Schema, entry.Example));
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

    private static JsonObject EmptySchema() => ObjectSchema();
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
    private static JsonObject NotesCreateSchema() => ObjectSchema(["path", "title", "content"], [("path", StringSchema()), ("title", StringSchema()), ("content", StringSchema())]);
    private static JsonObject WebSearchSchema() => ObjectSchema(["query"], [("query", StringSchema(512))]);
    private static JsonObject WebFetchSchema() => ObjectSchema(["url"], [("url", StringSchema(2048))]);
    private static JsonObject VolumeSchema() => ObjectSchema(["operation"], [("operation", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("get", "set", "increase", "decrease", "mute", "unmute") }), ("level", IntegerSchema(0, 100)), ("amount", IntegerSchema(1, 50))]);

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
