using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Tools;

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

public static partial class AgentToolDefinitionCatalog
{
    public const string CurrentVersion = "1.0.0";
    public const int MaximumModelTools = 32;
    // Shared by every file-path tool description; part of each description's hashed text.
    internal const string FilePathGuidance = " Use a workspace-relative path such as `src\\App.cs`; preserve a user-supplied absolute path only inside a directory granted for this session.";

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

    /// <summary>
    /// Builds the model-visible definitions from the descriptor table, in table order. Every row is
    /// validated here, so a malformed row fails catalog construction rather than reaching the model.
    /// </summary>
    private static IReadOnlyList<AgentToolDefinition> BuildDefinitions()
    {
        var rows = AgentToolDescriptorTable.All;
        var result = new List<AgentToolDefinition>(rows.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Ordinal != index || row.Version != CurrentVersion) throw new AgentProtocolException("invalid_tool_definition");
            if (!keys.Add(Key(row.Id, row.Version))) throw new AgentProtocolException("duplicate_tool_definition");
            if (!System.Text.RegularExpressions.Regex.IsMatch(row.Id, "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$") || row.Description.Length is 0 or > 2048 || !AgentToolScopes.All.Contains(row.Scope) || !row.ExtraScopes.All(AgentToolScopes.All.Contains)) throw new AgentProtocolException("invalid_tool_definition");
            var schema = row.InputSchema();
            if (!IsStrictObjectSchema(schema)) throw new AgentProtocolException("invalid_tool_definition");
            var example = row.ExampleArguments?.Invoke();
            if (example is not null && !ExampleMatches(schema, example)) throw new AgentProtocolException("invalid_tool_definition");
            result.Add(row.ToDefinition());
        }

        if (result.Count > MaximumModelTools) throw new AgentProtocolException("tool_catalog_capacity");
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

    internal static JsonObject GrepSchema() => ObjectSchema(["query"], GrepSchemaProperties());
    // Query bound mirrors FileToolLimits.MaxQueryLength in RuAgent.Broker (the
    // adapter is the authority; Protocol cannot reference the Broker layer).
    // Result caps mirror FileToolLimits.MaxResults the same way.
    // A tighter schema here false-rejects queries the adapter accepts.
    private static (string Name, JsonObject Schema)[] GrepSchemaProperties() =>
    [
        ("query", StringSchema(2048)),
        ("path", StringSchema()), ("file_glob", StringSchema()),
        ("case_sensitive", BooleanSchema()), ("is_regex", BooleanSchema()),
        ("max_results", IntegerSchema(1, 128)),
        // Paging bound mirrors FileToolLimits.MaxOffset in RuAgent.Broker (the
        // adapter is the authority; Protocol cannot reference the Broker layer).
        ("offset", IntegerSchema(0, 50000)),
    ];
    internal static JsonObject GlobSchema() => ObjectSchema(["pattern"], GlobSchemaProperties());
    private static (string Name, JsonObject Schema)[] GlobSchemaProperties() =>
    [
        ("pattern", StringSchema()),
        ("path", StringSchema()), ("max_results", IntegerSchema(1, 128)),
    ];
    internal static JsonObject FileListSchema() => ObjectSchema(properties: [("path", StringSchema()), ("max_entries", IntegerSchema(1, 512))]);
    internal static JsonObject FileReadSchema() => ObjectSchema(["path"], [("path", StringSchema()), ("offset", IntegerSchema(0)), ("limit", IntegerSchema(1, 4 * 1024 * 1024))]);
    internal static JsonObject HelpDocsSchema() => ObjectSchema(["query"], [("query", StringSchema(minLength: 1, maxLength: 256)), ("max_results", IntegerSchema(1, 5))]);

    // The catalog declares its own bounds, as every other schema here does. Sharing the
    // activity wire's constants would tie the model-visible contract to a display
    // contract, and the two would then have to change together for no reason.
    private const int MaximumTodoItems = 64;
    private const int MaximumTodoContentLength = 200;

    internal static JsonObject TodoWriteSchema() => ObjectSchema(["todos"], [("todos", TodoListSchema())]);

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
    internal static JsonObject WebSearchSchema() => ObjectSchema(["query"], [("query", StringSchema(512)), ("max_results", IntegerSchema(1, 10))]);
    internal static JsonObject WebFetchSchema() => ObjectSchema(["url"], [("url", StringSchema(2048)), ("offset", IntegerSchema(0, 4 * 1024 * 1024)), ("limit", IntegerSchema(1, 32_000))]);
    internal static JsonObject McpSchema() => ObjectSchema(
        ["op"],
        ("op", EnumSchema("list_servers", "list_tools", "call")),
        ("server", StringSchema(maxLength: 32, minLength: 1)),
        ("tool", StringSchema(maxLength: 128, minLength: 1)),
        ("arguments", StringSchema(maxLength: 64 * 1024)));
    // Bounds mirror ComputerLimits in RuAgent.Protocol; the Broker adapter is the authority.
    internal static JsonObject ComputerSchema() => ObjectSchema(
        ["action"],
        ("action", EnumSchema(ComputerActions.All.ToArray())),
        ("monitor", IntegerSchema(0, ComputerLimits.MaxMonitorIndex)),
        ("x", IntegerSchema(0, ComputerLimits.MaxScreenshotEdge)),
        ("y", IntegerSchema(0, ComputerLimits.MaxScreenshotEdge)),
        ("to_x", IntegerSchema(0, ComputerLimits.MaxScreenshotEdge)),
        ("to_y", IntegerSchema(0, ComputerLimits.MaxScreenshotEdge)),
        ("button", EnumSchema("left", "right", "middle")),
        ("count", IntegerSchema(1, 3)),
        ("direction", EnumSchema("up", "down", "left", "right")),
        ("amount", IntegerSchema(1, ComputerLimits.MaxScrollAmount)),
        ("text", StringSchema(maxLength: ComputerLimits.MaxTypeChars, minLength: 1)),
        ("keys", StringSchema(maxLength: ComputerLimits.MaxKeyChordChars, minLength: 1)),
        ("apps", ArraySchema(maxItems: ComputerLimits.MaxRequestedApps, itemMaxLength: ComputerLimits.MaxAppNameChars)),
        ("app", StringSchema(maxLength: ComputerLimits.MaxAppNameChars, minLength: 1)));
    internal static JsonObject SkillSchema() => ObjectSchema(
        [],
        ("name", StringSchema(minLength: 1, maxLength: 96)),
        ("query", StringSchema(minLength: 1, maxLength: 128)),
        ("expected_revision", StringSchema(maxLength: 64)),
        ("expected_body_sha256", StringSchema(maxLength: 64)));
    internal static JsonObject AskUserSchema() => ObjectSchema(
        ["question"],
        ("question", StringSchema(maxLength: 2048, minLength: 1)),
        ("options", ArraySchema(maxItems: 8, itemMaxLength: 512)));

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

    private static JsonObject WithDescription(JsonObject schema, string description)
    {
        schema["description"] = description;
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
