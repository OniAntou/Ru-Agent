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
        new("read", "Read one workspace file as UTF-8 text. Use offset and limit to page through long files." + FilePathGuidance, AgentToolScopes.SystemRead, FileReadSchema, () => new JsonObject { ["path"] = "README.md" }),
        new("list", "List one workspace directory's entries with metadata, bounded by max_entries." + FilePathGuidance, AgentToolScopes.WorkspaceRead, FileListSchema, () => new JsonObject { ["path"] = "." }),
        new("help", "Search Ru's bundled offline documentation for product behavior, architecture, configuration, tools, providers, and operating boundaries. Returns stable document IDs and excerpts only; it never reads workspace files.", AgentToolScopes.SystemRead, HelpDocsSchema, () => new JsonObject { ["query"] = "approval reasons" }),
        new("todo_write", "Replace the task list for this session. Send the complete list every time; it replaces the previous one. Use it to plan multi-step work and show progress: mark a task in_progress while working on it, completed the moment it is done, and keep at most one in_progress.", AgentToolScopes.SystemRead, TodoWriteSchema, () => new JsonObject { ["todos"] = new JsonArray(new JsonObject { ["content"] = "Read the failing test", ["status"] = "completed" }, new JsonObject { ["content"] = "Implement the validator", ["status"] = "in_progress" }) }),
        new("grep", "Search workspace file contents. Text or regex search with a glob filter and bounded results." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GrepSchema, () => new JsonObject { ["query"] = "TODO", ["path"] = "." }),
        new("glob", "Find workspace files by glob pattern. Bounded ordinal results with traversal caps." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GlobSchema, () => new JsonObject { ["pattern"] = "**/*.cs", ["path"] = "." }),
        new("write", "Create a new file or replace a whole file (op=write), delete (op=delete), or restore (op=restore). For partial changes to an existing file, prefer edit. Policy still controls mutation and overwrite." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, WriteSchema, () => new JsonObject { ["op"] = "write", ["path"] = "notes.txt", ["content"] = "new content" }),
        new("edit", "Preferred for changing an existing file. Replace one exact non-empty string." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, FileEditSchema, () => new JsonObject { ["path"] = "notes.txt", ["old_string"] = "old", ["new_string"] = "new" }),
        new("bash", "Run one bounded executable or shell script with an argument vector and explicit network intent.", AgentToolScopes.TerminalExecute, BashSchema, () => new JsonObject { ["mode"] = "run", ["executable"] = "git", ["arguments"] = new JsonArray("status"), ["working_directory"] = "." }),
        new("web_search", "Search the public web. One configured engine answers each query; if it fails or its layout cannot be read, the call fails rather than being retried elsewhere. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkSearch, WebSearchSchema, () => new JsonObject { ["query"] = "latest dotnet release" }),
        new("web_fetch", "Fetch one public web page as sanitized text. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkFetch, WebFetchSchema, () => new JsonObject { ["url"] = "https://example.com" }),
        new("skill", "Load one workspace skill's full SKILL.md workflow. Skills are prompt guidance discovered from the workspace skill roots; the prompt lists only name and description, and the model calls this tool to load the body on demand. allowed-tools metadata is descriptive only and never grants a tool, scope, path, network permission, or approval.", AgentToolScopes.SystemRead, SkillSchema, () => new JsonObject { ["name"] = "pdf.analyzer" }),
    ];

    private static readonly IReadOnlyList<AgentToolDefinition> Definitions = BuildDefinitions();
    private static readonly IReadOnlyDictionary<string, AgentToolDefinition> ByKey = Definitions.ToDictionary(item => Key(item.ToolId, item.ToolVersion), StringComparer.Ordinal);

    public static IReadOnlyList<AgentToolDefinition> Get() => Definitions;
    public static AgentToolDefinition? Find(string toolId, string toolVersion) => toolId is not null && toolVersion is not null && ByKey.TryGetValue(Key(toolId, toolVersion), out var definition) ? definition : null;

    public static string ComputeSchemaDigest(JsonObject schema) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalSchemaJson(schema)))).ToLowerInvariant();

    /// <summary>
    /// The one wire form of a tool schema: compact JSON with recursively sorted
    /// keys. Every capability that carries <c>input_schema_json</c> serializes
    /// through this so the string and <see cref="ComputeSchemaDigest"/> hash the
    /// same bytes wherever they are checked.
    /// </summary>
    public static string CanonicalSchemaJson(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Canonicalize(schema).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode Canonicalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var pair in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                sorted[pair.Key] = pair.Value is null ? null : Canonicalize(pair.Value);
            }

            return sorted;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(item is null ? null : Canonicalize(item));
            }

            return result;
        }

        // Primitives are re-parsed rather than re-parented: JsonNode allows one
        // parent, and the digest input may be any live document.
        return JsonNode.Parse(node.ToJsonString())!;
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

    private static JsonObject WriteSchema() => ObjectSchema(["op"], WriteSchemaProperties());
    private static JsonObject BashSchema() => ObjectSchema(["mode"], BashSchemaProperties());
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

    // The catalog declares its own bounds, as every other schema here does. Sharing the
    // activity wire's constants would tie the model-visible contract to a display
    // contract, and the two would then have to change together for no reason.
    private const int MaximumTodoItems = 64;
    private const int MaximumTodoContentLength = 200;

    private static JsonObject TodoWriteSchema() => ObjectSchema(["todos"], [("todos", TodoListSchema())]);

    /// <summary>
    /// The task list argument.
    /// </summary>
    /// <remarks>
    /// The schema enforces shape only: an array of objects carrying exactly these two
    /// keys. It deliberately does NOT constrain content length or the status values, even
    /// though it could. Doing so would make those two rejections unreachable in the
    /// executor, and the model would see a generic `invalid_arguments` instead of
    /// `todo_content_empty` or `todo_status_unknown` — leaving it no way to tell what was
    /// wrong with the list it just sent. A stable, specific code is worth more than a
    /// schema that says the same thing earlier and less helpfully.
    /// </remarks>
    private static JsonObject TodoListSchema() => new()
    {
        ["type"] = "array",
        ["maxItems"] = MaximumTodoItems,
        ["items"] = ObjectSchema(
            ["content", "status"],
            ("content", StringSchema(maxLength: MaximumTodoContentLength)),
            ("status", StringSchema(maxLength: 32))),
    };
    private static JsonObject TerminalRunSchema() => ObjectSchema(["executable"], [("executable", StringSchema(TerminalLimits.MaxExecutableChars)), ("arguments", ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars)), ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)), ("timeout_ms", IntegerSchema(1, TerminalLimits.HardMaxTimeoutMs)), ("network_intent", BooleanSchema()), ..TerminalWriteSetProperties()]);
    private static JsonObject TerminalShellSchema() => ObjectSchema(["script"], [("script", StringSchema(TerminalLimits.MaxScriptChars)), ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)), ("timeout_ms", IntegerSchema(1, TerminalLimits.HardMaxTimeoutMs)), ("network_intent", BooleanSchema()), ..TerminalWriteSetProperties()]);
    private static JsonObject WebSearchSchema() => ObjectSchema(["query"], [("query", StringSchema(512)), ("max_results", IntegerSchema(1, 10))]);
    private static JsonObject WebFetchSchema() => ObjectSchema(["url"], [("url", StringSchema(2048))]);
    private static JsonObject SkillSchema() => ObjectSchema(["name"], [("name", StringSchema(minLength: 1, maxLength: 96))]);

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
