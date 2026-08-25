using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public sealed record AgentModelToolContract(
    string ToolId,
    string ToolVersion,
    JsonObject InputSchema,
    JsonObject? ExampleArguments)
{
    public string? Hint => ExampleArguments?.ToJsonString();
}

public static class AgentModelToolContractCatalog
{
    public const string CurrentVersion = "1.0.0";

    private sealed record Definition(
        string ToolId,
        Func<JsonObject> CreateInputSchema,
        Func<JsonObject>? CreateExampleArguments = null);

    private static readonly Definition[] Definitions =
    [
        new("system.time", EmptySchema),
        new("system.status", EmptySchema),
        new("system.volume", VolumeSchema, () => new JsonObject
        {
            ["operation"] = "set",
            ["level"] = 30,
        }),
        new("app.list", EmptySchema),
        new("files.edit", FileEditSchema, () => new JsonObject
        {
            ["path"] = "ten-file-trong-workspace",
            ["old_string"] = "noi-dung-cu",
            ["new_string"] = "noi-dung-moi",
        }),
        new("files.list", FileListSchema, () => new JsonObject
        {
            ["path"] = "thu-muc-trong-workspace",
            ["max_entries"] = 32,
        }),
        new("files.glob", FileGlobSchema, () => new JsonObject
        {
            ["path"] = "thu-muc-trong-workspace",
            ["pattern"] = "*.txt",
        }),
        new("files.search", FileSearchSchema, () => new JsonObject
        {
            ["path"] = "thu-muc-trong-workspace",
            ["query"] = "tim-kiem",
        }),
        new("files.read", FileReadSchema, () => new JsonObject
        {
            ["path"] = "ten-file-trong-workspace",
            ["offset"] = 0,
            ["limit"] = 1024,
        }),
        new("files.write", FileWriteSchema, () => new JsonObject
        {
            ["path"] = "ten-file-trong-workspace",
            ["content"] = "noi-dung-moi",
        }),
        new("terminal.run", TerminalRunSchema, () => new JsonObject
        {
            ["executable"] = "git",
            ["arguments"] = new JsonArray("status"),
            ["working_directory"] = ".",
            ["network"] = false,
        }),
        new("terminal.shell", TerminalShellSchema, () => new JsonObject
        {
            ["script"] = "lenh-shell",
            ["working_directory"] = ".",
            ["network"] = false,
        }),
        new("notes.create", NotesCreateSchema, () => new JsonObject
        {
            ["path"] = "ghi-chu.md",
            ["title"] = "Tieu de",
            ["content"] = "Noi dung",
        }),
    ];

    private static readonly IReadOnlyDictionary<string, Definition> ById =
        Definitions.ToDictionary(item => item.ToolId, StringComparer.Ordinal);

    public static IReadOnlyList<string> ToolIds { get; } =
        Array.AsReadOnly(Definitions.Select(item => item.ToolId).ToArray());

    public static AgentModelToolContract Get(string toolId, string toolVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        if (!string.Equals(toolVersion, CurrentVersion, StringComparison.Ordinal)
            || !ById.TryGetValue(toolId, out var definition))
        {
            throw new KeyNotFoundException("tool_contract_not_found");
        }

        return new AgentModelToolContract(
            definition.ToolId,
            CurrentVersion,
            definition.CreateInputSchema(),
            definition.CreateExampleArguments?.Invoke());
    }

    public static AgentModelToolContract? Find(string toolId, string toolVersion) =>
        string.Equals(toolVersion, CurrentVersion, StringComparison.Ordinal)
        && ById.TryGetValue(toolId, out var definition)
            ? new AgentModelToolContract(
                definition.ToolId,
                CurrentVersion,
                definition.CreateInputSchema(),
                definition.CreateExampleArguments?.Invoke())
            : null;

    private static JsonObject EmptySchema() => ObjectSchema();

    private static JsonObject FileListSchema() => ObjectSchema(
        properties: [("path", StringSchema()), ("max_entries", IntegerSchema(minimum: 1, maximum: 512))]);

    private static JsonObject FileGlobSchema() => ObjectSchema(
        required: ["pattern"],
        properties: [("path", StringSchema()), ("pattern", StringSchema()), ("max_results", IntegerSchema(minimum: 1, maximum: 64))]);

    private static JsonObject FileSearchSchema() => ObjectSchema(
        required: ["query"],
        properties:
        [
            ("path", StringSchema()),
            ("query", StringSchema()),
            ("file_glob", StringSchema()),
            ("case_sensitive", BooleanSchema()),
            ("max_results", IntegerSchema(minimum: 1, maximum: 64)),
        ]);

    private static JsonObject FileReadSchema() => ObjectSchema(
        required: ["path"],
        properties:
        [
            ("path", StringSchema()),
            ("offset", IntegerSchema(minimum: 0)),
            ("limit", IntegerSchema(minimum: 1, maximum: 256 * 1024)),
        ]);

    private static JsonObject FileWriteSchema() => ObjectSchema(
        required: ["path", "content"],
        properties:
        [
            ("path", StringSchema()),
            ("content", StringSchema()),
            ("overwrite", BooleanSchema()),
        ]);

    private static JsonObject FileEditSchema() => ObjectSchema(
        required: ["path", "old_string"],
        properties:
        [
            ("path", StringSchema()),
            ("old_string", StringSchema()),
            ("new_string", StringSchema()),
            ("expected_occurrences", IntegerSchema(minimum: 1)),
        ]);

    private static JsonObject TerminalRunSchema() => ObjectSchema(
        required: ["executable"],
        properties:
        [
            ("shell", StringSchema()),
            ("executable", StringSchema()),
            ("arguments", new JsonObject { ["type"] = "array" }),
            ("working_directory", StringSchema()),
            ("network", BooleanSchema()),
        ]);

    private static JsonObject TerminalShellSchema() => ObjectSchema(
        required: ["script"],
        properties:
        [
            ("shell", StringSchema()),
            ("script", StringSchema()),
            ("working_directory", StringSchema()),
            ("network", BooleanSchema()),
        ]);

    private static JsonObject NotesCreateSchema() => ObjectSchema(
        required: ["path", "title", "content"],
        properties:
        [
            ("path", StringSchema()),
            ("title", StringSchema()),
            ("content", StringSchema()),
        ]);

    private static JsonObject VolumeSchema() => ObjectSchema(
        required: ["operation"],
        properties:
        [
            ("operation", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("get", "set", "increase", "decrease", "mute", "unmute"),
            }),
            ("level", IntegerSchema(minimum: 0, maximum: 100)),
            ("amount", IntegerSchema(minimum: 1, maximum: 50)),
        ]);

    private static JsonObject ObjectSchema(
        string[]? required = null,
        params (string Name, JsonObject Schema)[] properties)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject(
                properties.Select(property =>
                    KeyValuePair.Create<string, JsonNode?>(property.Name, property.Schema))),
        };
        if (required is { Length: > 0 })
        {
            schema["required"] = new JsonArray(
                required.Select(item => (JsonNode?)item).ToArray());
        }

        return schema;
    }

    private static JsonObject StringSchema() => new() { ["type"] = "string" };

    private static JsonObject BooleanSchema() => new() { ["type"] = "boolean" };

    private static JsonObject IntegerSchema(int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum is not null)
        {
            schema["minimum"] = minimum.Value;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum.Value;
        }

        return schema;
    }
}
