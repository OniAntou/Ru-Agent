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
    private const string FilePathGuidance = " Use a workspace-relative path such as `src\\App.cs`; preserve a user-supplied absolute path only inside a directory granted for this session.";

    private sealed record Entry(string Id, string Description, string Scope, Func<JsonObject> Schema, Func<JsonObject>? Example = null);

    private static readonly IReadOnlyList<Entry> Entries =
    [
        new("read", "Read one workspace file as UTF-8 text. Use offset and limit to page through long files. If a result has projection_has_more=true, continue the same path at projection_next_offset; offsets count source bytes. On read_limit_too_small retry with limit at least 4; invalid_utf8_offset means the offset is inside a character, so use the returned cursor." + FilePathGuidance, AgentToolScopes.SystemRead, FileReadSchema, () => new JsonObject { ["path"] = "src\\App.cs" }),
        new("list", "List one workspace directory's entries with metadata, bounded by max_entries." + FilePathGuidance, AgentToolScopes.WorkspaceRead, FileListSchema, () => new JsonObject { ["path"] = "." }),
        new("help", "Search Ru's bundled offline documentation for product behavior, architecture, configuration, tools, providers, and operating boundaries. Returns stable document IDs and excerpts only; it never reads workspace files.", AgentToolScopes.SystemRead, HelpDocsSchema, () => new JsonObject { ["query"] = "approval reasons" }),
        new("todo_write", "Replace the task list for this session. Send the complete list every time; it replaces the previous one. Use it to plan multi-step work and show progress: mark a task in_progress while working on it, completed the moment it is done, and keep at most one in_progress.", AgentToolScopes.SystemRead, TodoWriteSchema, () => new JsonObject { ["todos"] = new JsonArray(new JsonObject { ["content"] = "Read the failing test", ["status"] = "completed" }, new JsonObject { ["content"] = "Implement the validator", ["status"] = "in_progress" }) }),
        new("grep", "Search workspace file contents. Text or regex search with a glob filter and bounded results. **/ matches zero or more directories. Previews include the match; column is the absolute 1-based line column and preview_column names the preview's first column. Continue the same query and filters at next_offset when present; if projection_has_more=true, use projection_next_offset instead. Cursors count match results and each page re-scans files. scan_incomplete means a scan/time bound or regex timeout prevented full coverage; narrow path/query rather than paging blindly. For exact numbers use is_regex word boundaries (\\b41\\b) so fixture hashes do not match as substrings." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GrepSchema, () => new JsonObject { ["query"] = "TODO", ["path"] = "." }),
        new("glob", "Find workspace files by glob pattern. Bounded ordinal results with traversal caps." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GlobSchema, () => new JsonObject { ["pattern"] = "**/*.cs", ["path"] = "." }),
        new("write", "Create a file with op=write. To replace an existing file, use op=write with overwrite=true. Use op=delete or op=restore for those operations; use edit for a partial change. Policy still controls mutation and overwrite." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, WriteSchema, () => new JsonObject { ["op"] = "write", ["path"] = "src\\notes.txt", ["content"] = "new content" }),
        new("edit", "Change an existing file by replacing one exact, non-empty string copied from a fresh read; whitespace must match. Put the copied text in old_string and its replacement in new_string." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, FileEditSchema, () => new JsonObject { ["path"] = "src\\notes.txt", ["old_string"] = "old", ["new_string"] = "new" }),
        new("bash", "Run one bounded executable or shell script with an argument vector and explicit network intent. In run mode, executable is separate from arguments; for example, executable=node and arguments=[\"-e\",\"console.log('hi')\"] (do not include node in arguments). For a script use mode=shell with script; on Windows the default shell is powershell.exe (Windows PowerShell 5.1). Use PowerShell syntax, such as Get-Content -Tail, rather than assuming Unix head/tail commands exist. Output is head/tail bounded: stdout_head/stdout_tail carry the start/end while stdout_bytes and stdout_dropped_bytes name the total and swallowed middle. Do not rerun a command just to inspect output; use mode read_output with the same execution_id and offset/limit, or projection_next_offset when the displayed page was shortened. Output past the retained window is kept in a session spool file (named by spool_path on the receipt) and read_output pages it the same way. Terminal cursors count UTF-16 characters. Set background true for long-running commands (dev servers, watch builds): the call returns immediately with execution_id and state running, mode read_output pages live output while it runs, and mode kill stops it by execution_id. Background tasks are session-scoped, capped per session, and die at their lifetime limit or when the Broker exits. On Windows powershell.exe (5.1) always exists while pwsh exists only with PowerShell 7 installed; a missing name fails closed as executable_not_found instead of asking.", AgentToolScopes.TerminalExecute, BashSchema, () => new JsonObject { ["mode"] = "run", ["executable"] = "node", ["arguments"] = new JsonArray("-e", "console.log('hi')"), ["working_directory"] = "." }),
        new("web_search", "Search the public web. The configured engine races a fixed set of free fallback engines and the first engine with usable results answers, so a slow or blocked primary no longer delays the result. If every engine fails or none of their layouts can be read, the call fails with a bounded error rather than returning an empty answer. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkSearch, WebSearchSchema, () => new JsonObject { ["query"] = "latest dotnet release" }),
        new("web_fetch", "Fetch public HTML, Markdown, JSON or source text with code layout preserved. Downloads are bounded; output defaults to 32000 characters. For long documents, pass next_offset from the result as offset to read the next page; when projection_has_more=true, prefer projection_next_offset; offsets count UTF-16 characters in extracted text and each call fetches the source again. Optional limit is 1-32000. Page boundaries preserve whole Unicode characters. On web_limit_too_small retry with limit at least 2; web_offset_invalid_boundary means use a returned cursor instead of an offset inside an emoji. CAPTCHA, empty JavaScript shells and unsupported binary formats fail with named errors. Results are explicitly untrusted.", AgentToolScopes.NetworkFetch, WebFetchSchema, () => new JsonObject { ["url"] = "https://example.com" }),
        new("skill", "Load one workspace skill's full SKILL.md workflow. Skills are prompt guidance discovered from the workspace skill roots; the prompt lists only name and description, and the model calls this tool to load the body on demand. allowed-tools metadata is descriptive only and never grants a tool, scope, path, network permission, or approval.", AgentToolScopes.SystemRead, SkillSchema, () => new JsonObject { ["name"] = "pdf.analyzer" }),
        new("ask_user", "Ask the user a question and wait for the answer. Use it only when a decision is genuinely the user's and no tool or file can resolve it. The run parks until the user answers, and the answer arrives as this tool's result. options are optional suggested answers.", AgentToolScopes.UserInput, AskUserSchema, () => new JsonObject { ["question"] = "Which environment should I use?", ["options"] = new JsonArray("staging", "production") }),
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
    private static JsonObject GlobSchema() => ObjectSchema(["pattern"], GlobSchemaProperties());
    private static (string Name, JsonObject Schema)[] GlobSchemaProperties() =>
    [
        ("pattern", StringSchema()),
        ("path", StringSchema()), ("max_results", IntegerSchema(1, 128)),
    ];
    private static (string Name, JsonObject Schema)[] WriteSchemaProperties() =>
    [
        ("op", EnumSchema(["write", "delete", "restore"])),
        ("path", StringSchema()), ("content", StringSchema()), ("overwrite", BooleanSchema()),
        ("recovery_id", StringSchema(maxLength: 256)),
    ];
    private static (string Name, JsonObject Schema)[] BashSchemaProperties() =>
    [
        ("mode", EnumSchema(["run", "shell", "read_output", "kill"])),
        ("executable", StringSchema(TerminalLimits.MaxExecutableChars)),
        ("arguments", ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars)),
        ("script", StringSchema(TerminalLimits.MaxScriptChars)),
        ("working_directory", StringSchema(TerminalLimits.MaxWorkingDirectoryChars)),
        // The schema carries the background ceiling; the evaluator enforces the
        // tighter foreground hard max when background is not set.
        ("timeout_ms", IntegerSchema(1, TerminalLimits.BackgroundMaxLifetimeMs)),
        ("network_intent", BooleanSchema()),
        .. TerminalWriteSetProperties(),
        ("background", BooleanSchema()),
        ("execution_id", StringSchema(TerminalLimits.MaxExecutionIdChars, minLength: 1)),
        // Paging bound mirrors the spool retention ceiling in RuAgent.Broker,
        // counted in UTF-16 characters: the in-memory window plus the spilled
        // file. Protocol cannot reference the Broker layer.
        ("offset", IntegerSchema(0, TerminalLimits.MaxSpoolFileChars)),
        ("limit", IntegerSchema(1, TerminalLimits.MaxSpoolPageChars)),
    ];
    private static JsonObject FileListSchema() => ObjectSchema(properties: [("path", StringSchema()), ("max_entries", IntegerSchema(1, 512))]);
    private static JsonObject FileReadSchema() => ObjectSchema(["path"], [("path", StringSchema()), ("offset", IntegerSchema(0)), ("limit", IntegerSchema(1, 512 * 1024))]);
    private static JsonObject FileEditSchema() => ObjectSchema(["path", "old_string"], [("path", StringSchema()), ("old_string", StringSchema(minLength: 1)), ("new_string", StringSchema()), ("expected_occurrences", IntegerSchema(1, 1024))]);
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
    private static JsonObject WebSearchSchema() => ObjectSchema(["query"], [("query", StringSchema(512)), ("max_results", IntegerSchema(1, 10))]);
    private static JsonObject WebFetchSchema() => ObjectSchema(["url"], [("url", StringSchema(2048)), ("offset", IntegerSchema(0, 4 * 1024 * 1024)), ("limit", IntegerSchema(1, 32_000))]);
    private static JsonObject SkillSchema() => ObjectSchema(["name"], [("name", StringSchema(minLength: 1, maxLength: 96))]);
    private static JsonObject AskUserSchema() => ObjectSchema(
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
