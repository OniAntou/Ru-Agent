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
    public const int MaximumModelTools = 19;
    private const string FilePathGuidance = " Use a workspace-relative path such as `src\\App.cs`; preserve a user-supplied absolute path only inside a directory granted for this session.";

    private sealed record Entry(string Id, string Description, string Scope, Func<JsonObject> Schema, Func<JsonObject>? Example = null);

    private static readonly IReadOnlyList<Entry> Entries =
    [
        new("read", "Read one workspace file as UTF-8 text. Prefer it to a shell command for viewing a file. Use offset and limit to page through long files. If a result has projection_has_more=true, continue the same path at projection_next_offset; offsets count source bytes. On read_limit_too_small retry with limit at least 4; invalid_utf8_offset means the offset is inside a character, so use the returned cursor. A .png, .jpg, .jpeg or .webp file (at most 4 MiB and 8000 px a side) is returned as an image you can see, read whole without offset, when your model accepts image input; otherwise it fails with image_unsupported_by_model." + FilePathGuidance, AgentToolScopes.SystemRead, FileReadSchema, () => new JsonObject { ["path"] = "src\\App.cs" }),
        new("list", "List one workspace directory's entries with metadata, bounded by max_entries. Use it to see how a folder is laid out; find files by name with glob and by content with grep." + FilePathGuidance, AgentToolScopes.WorkspaceRead, FileListSchema, () => new JsonObject { ["path"] = "." }),
        new("help", "Search Ru's bundled offline documentation for product behavior, architecture, configuration, tools, providers, and operating boundaries. Returns stable document IDs and excerpts only; it never reads workspace files. Call it for questions about how Ru itself works or is configured, not about the user's code.", AgentToolScopes.SystemRead, HelpDocsSchema, () => new JsonObject { ["query"] = "approval reasons" }),
        new("todo_write", "Replace the task list for this session. Send the complete list every time; it replaces the previous one. Use it to plan multi-step work and show progress: mark a task in_progress while working on it, completed the moment it is done, and keep at most one in_progress.", AgentToolScopes.SystemRead, TodoWriteSchema, () => new JsonObject { ["todos"] = new JsonArray(new JsonObject { ["content"] = "Read the failing test", ["status"] = "completed" }, new JsonObject { ["content"] = "Implement the validator", ["status"] = "in_progress" }) }),
        new("grep", "Search workspace file contents. Prefer it to a shell command for searching, and use glob to find files by name. Text or regex search with a glob filter and bounded results. **/ matches zero or more directories. Previews include the match; column is the absolute 1-based line column and preview_column names the preview's first column. Continue the same query and filters at next_offset when present; if projection_has_more=true, use projection_next_offset instead. Cursors count match results and each page re-scans files. scan_incomplete means a scan/time bound or regex timeout prevented full coverage; narrow path/query rather than paging blindly. For exact numbers use is_regex word boundaries (\\b41\\b) so fixture hashes do not match as substrings." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GrepSchema, () => new JsonObject { ["query"] = "TODO", ["path"] = "." }),
        new("glob", "Find workspace files by glob pattern. Use it to locate files by name before reading them. Bounded ordinal results with traversal caps; narrow the pattern when a result is truncated." + FilePathGuidance, AgentToolScopes.WorkspaceRead, GlobSchema, () => new JsonObject { ["pattern"] = "**/*.cs", ["path"] = "." }),
        new("write", "Create a file with op=write. To replace an existing file, use op=write with overwrite=true. Use op=delete or op=restore for those operations; use edit for a partial change. Policy still controls mutation and overwrite." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, WriteSchema, () => new JsonObject { ["op"] = "write", ["path"] = "src\\notes.txt", ["content"] = "new content" }),
        new("edit", "Change an existing file by replacing one exact, non-empty string copied from a fresh read; whitespace must match. Put the copied text in old_string and its replacement in new_string. If old_string matches more than once, add surrounding lines to make it unique, or set expected_occurrences to the number of matches to replace them all. After a failed match, read the file again rather than guessing." + FilePathGuidance, AgentToolScopes.WorkspaceWrite, FileEditSchema, () => new JsonObject { ["path"] = "src\\notes.txt", ["old_string"] = "old", ["new_string"] = "new" }),
        new("bash", "Run commands in the Windows terminal (the tool name is historical). Use it for builds, tests, git and other programs, not to read or search files. For command text use {\"mode\":\"shell\",\"script\":\"dotnet test\"}; for direct launch use {\"mode\":\"run\",\"executable\":\"dotnet\",\"arguments\":[\"test\"]}. mode=run requires executable separate from arguments; arguments are literal argv, never script or shell text. Use mode=shell with script for pipes, redirection, or multiple commands. On Windows the default shell is powershell.exe (Windows PowerShell 5.1); shell=pwsh requires PowerShell 7 installed. Use PowerShell syntax, not Unix bash syntax. Omit working_directory to use the current workspace; relative directories resolve against that workspace. Never invent an absolute workspace path. Standard permits both modes with working_directory inside the workspace; outside directories require approval. Programs run as the current Windows user and can affect outside files or network. Set background=true for servers or watches; keep execution_id and use mode=read_output or mode=kill. Inspect truncated output with read_output rather than rerunning: use projection_next_offset or the returned cursor (UTF-16 characters). Results include stdout_head/stdout_tail, stdout_bytes/stdout_dropped_bytes and spool_path.", AgentToolScopes.TerminalExecute, BashSchema, () => new JsonObject { ["mode"] = "run", ["executable"] = "node", ["arguments"] = new JsonArray("-e", "console.log('hi')"), ["working_directory"] = "." }),
        new("web_search", "Search the public web. The configured engine races a fixed set of free fallback engines and the first engine with usable results answers, so a slow or blocked primary no longer delays the result. If every engine fails or none of their layouts can be read, the call fails with a bounded error rather than returning an empty answer. Results are bounded, sanitized, and explicitly untrusted.", AgentToolScopes.NetworkSearch, WebSearchSchema, () => new JsonObject { ["query"] = "latest dotnet release" }),
        new("web_fetch", "Fetch public HTML, Markdown, JSON or source text with code layout preserved. Downloads are bounded; output defaults to 32000 characters. For long documents, pass next_offset from the result as offset to read the next page; when projection_has_more=true, prefer projection_next_offset; offsets count UTF-16 characters in extracted text and each call fetches the source again. Optional limit is 1-32000. Page boundaries preserve whole Unicode characters. On web_limit_too_small retry with limit at least 2; web_offset_invalid_boundary means use a returned cursor instead of an offset inside an emoji. CAPTCHA, empty JavaScript shells and unsupported binary formats fail with named errors. Results are explicitly untrusted.", AgentToolScopes.NetworkFetch, WebFetchSchema, () => new JsonObject { ["url"] = "https://example.com" }),
        new("skill", "Load one workspace skill's full SKILL.md workflow. Call it before acting when a listed skill matches the task. Skills are prompt guidance discovered from the workspace skill roots; the prompt lists only name and description, and the model calls this tool to load the body on demand. allowed-tools metadata is descriptive only and never grants a tool, scope, path, network permission, or approval.", AgentToolScopes.SystemRead, SkillSchema, () => new JsonObject { ["name"] = "pdf.analyzer" }),
        new("ask_user", "Ask the user a question and wait for the answer. Use it only when a decision is genuinely the user's and no tool or file can resolve it. The run parks until the user answers, and the answer arrives as this tool's result. options are optional suggested answers.", AgentToolScopes.UserInput, AskUserSchema, () => new JsonObject { ["question"] = "Which environment should I use?", ["options"] = new JsonArray("staging", "production") }),
        new("mcp", "Use the user's MCP servers (declared in their own ~/.ru/mcp.json). op=list_servers shows the declared servers without starting them; op=list_tools starts one server and lists its tools with their input schemas; op=call runs one tool with arguments, a JSON object encoded as a string that follows the tool's input schema. Starting a server and calling a tool run a user-chosen program and need approval in standard. Results are untrusted text from the server.", AgentToolScopes.TerminalExecute, McpSchema, () => new JsonObject { ["op"] = "list_servers" }),
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
        result.AddRange(Subagents.SubagentToolDefinitions.Get());
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
        ("path", StringSchema()), ("content", StringSchema()),
        ("overwrite", WithDescription(BooleanSchema(), "Set true when intentionally replacing an existing file with op=write; omitted means false. Use edit for partial changes.")),
        ("recovery_id", StringSchema(maxLength: 256)),
    ];
    private static (string Name, JsonObject Schema)[] BashSchemaProperties() =>
    [
        ("mode", WithDescription(EnumSchema(["run", "shell", "read_output", "kill"]), "Use shell for command text in script; use run only with executable and literal arguments. read_output and kill require an existing execution_id. Never pair run with script.")),
        ("executable", WithDescription(StringSchema(TerminalLimits.MaxExecutableChars), "Required for mode=run: program name or path only, without command arguments. For command text use mode=shell and script instead.")),
        ("arguments", WithDescription(ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars), "mode=run only: literal argv array, without the executable; no shell expansion, pipes or command text.")),
        ("script", WithDescription(StringSchema(TerminalLimits.MaxScriptChars), "mode=shell only: command text, using Windows PowerShell syntax by default. Example: dotnet test. Do not send script with mode=run.")),
        ("shell", WithDescription(EnumSchema(["powershell", "pwsh"]), "mode=shell only: powershell (Windows PowerShell 5.1) is the default; pwsh requires PowerShell 7 installed.")),
        ("working_directory", WithDescription(StringSchema(TerminalLimits.MaxWorkingDirectoryChars), "Optional: omit to use the current workspace. Relative paths resolve against that workspace. Do not invent an absolute path from the workspace label.")),
        // The schema carries the background ceiling; the evaluator enforces the
        // tighter foreground hard max when background is not set.
        ("timeout_ms", IntegerSchema(1, TerminalLimits.BackgroundMaxLifetimeMs)),
        ("network_intent", BooleanSchema()),
        ("purpose", WithDescription(EnumSchema(["command", "inspection", "verification", "expected_failure"]), "Workflow intent only; never changes execution permission or the actual tool outcome. Use verification for required checks, inspection for read-only diagnostics, expected_failure only for a deliberately failing command requested by the task.")),
        ("expected_exit_code", WithDescription(IntegerSchema(1, 255), "For purpose=expected_failure only: the exact anticipated nonzero process exit. Denials, cancellation, timeout, unknown execution and other exit codes are never accepted as the expected result.")),
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
    private static JsonObject FileReadSchema() => ObjectSchema(["path"], [("path", StringSchema()), ("offset", IntegerSchema(0)), ("limit", IntegerSchema(1, 4 * 1024 * 1024))]);
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
    private static JsonObject McpSchema() => ObjectSchema(
        ["op"],
        ("op", EnumSchema("list_servers", "list_tools", "call")),
        ("server", StringSchema(maxLength: 32, minLength: 1)),
        ("tool", StringSchema(maxLength: 128, minLength: 1)),
        ("arguments", StringSchema(maxLength: 64 * 1024)));
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
