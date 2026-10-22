using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>An environment value: literal text, or a Windows Credential Manager entry name.</summary>
public sealed record McpEnvironmentValue(string? Literal, string? Credential);

/// <summary>One stdio MCP server the user declared.</summary>
public sealed record McpServerDefinition(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, McpEnvironmentValue> Environment,
    string? Description,
    TimeSpan CallTimeout);

/// <summary>
/// MCP servers from <c>%USERPROFILE%\.ru\mcp.json</c> (ADR 0025). Only the user's own file is
/// read, never a workspace file: a model in <c>standard</c> may write workspace files, and a
/// server it declared would run any program the next time the tool is used. The file is a
/// structured protected path, so the model cannot write it in plan or standard either.
/// </summary>
/// <remarks>
/// <code>{"servers":{"github":{"command":"npx.cmd","args":["-y","@modelcontextprotocol/server-github"],
///   "env":{"GITHUB_TOKEN":{"credential":"ru-mcp-github"}},"description":"GitHub issues and PRs"}}}</code>
/// Unreadable, oversized or malformed files contribute nothing; an invalid entry is skipped, and
/// a disabled one (<c>"enabled": false</c>) is left out.
/// </remarks>
public sealed class McpConfiguration
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxServers = 16;
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = 4096;
    public const int MaxCommandLength = 1024;
    public const int MaxEnvironmentEntries = 32;
    public const int MaxDescriptionLength = 256;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex ServerName = new("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    public static McpConfiguration Empty { get; } = new([]);

    public McpConfiguration(IReadOnlyList<McpServerDefinition> servers) => Servers = servers;

    public IReadOnlyList<McpServerDefinition> Servers { get; }

    public bool IsEmpty => Servers.Count == 0;

    public McpServerDefinition? Find(string name) =>
        Servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.Ordinal));

    public static string PathFor(string userHome) => Path.Combine(userHome, ".ru", "mcp.json");

    public static McpConfiguration Load(string? userHome)
    {
        if (string.IsNullOrWhiteSpace(userHome)) return Empty;
        try
        {
            var info = new FileInfo(PathFor(userHome));
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return Empty;
            return Parse(File.ReadAllText(info.FullName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    internal static McpConfiguration Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var result = new List<McpServerDefinition>();
        foreach (var entry in servers.EnumerateObject())
        {
            if (result.Count >= MaxServers) break;
            if (TryReadServer(entry.Name, entry.Value) is { } server) result.Add(server);
        }

        return result.Count == 0 ? Empty : new McpConfiguration(result);
    }

    private static McpServerDefinition? TryReadServer(string name, JsonElement entry)
    {
        if (!ServerName.IsMatch(name) || entry.ValueKind != JsonValueKind.Object) return null;
        if (entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return null;
        if (Text(entry, "command") is not { Length: > 0 and <= MaxCommandLength } command || command.Contains('\0')) return null;

        var arguments = new List<string>();
        if (entry.TryGetProperty("args", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() > MaxArguments) return null;
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                var value = item.GetString()!;
                if (value.Length > MaxArgumentLength || value.Contains('\0')) return null;
                arguments.Add(value);
            }
        }

        string? workingDirectory = null;
        if (entry.TryGetProperty("cwd", out var cwd) && cwd.ValueKind != JsonValueKind.Null)
        {
            if (cwd.ValueKind != JsonValueKind.String || !Path.IsPathFullyQualified(cwd.GetString()!)) return null;
            workingDirectory = cwd.GetString();
        }

        var environment = new Dictionary<string, McpEnvironmentValue>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("env", out var env))
        {
            if (env.ValueKind != JsonValueKind.Object) return null;
            foreach (var variable in env.EnumerateObject())
            {
                if (environment.Count >= MaxEnvironmentEntries || !EnvironmentName.IsMatch(variable.Name)) return null;
                if (variable.Value.ValueKind == JsonValueKind.String && !variable.Value.GetString()!.Contains('\0'))
                {
                    environment[variable.Name] = new McpEnvironmentValue(variable.Value.GetString(), null);
                }
                else if (variable.Value.ValueKind == JsonValueKind.Object
                    && Text(variable.Value, "credential") is { Length: > 0 and <= 256 } credential)
                {
                    environment[variable.Name] = new McpEnvironmentValue(null, credential);
                }
                else
                {
                    return null;
                }
            }
        }

        var description = Text(entry, "description");
        if (description is { Length: > MaxDescriptionLength }) description = description[..MaxDescriptionLength];

        var timeout = DefaultCallTimeout;
        if (entry.TryGetProperty("timeout_seconds", out var seconds))
        {
            if (!seconds.TryGetInt32(out var value) || value < 1) return null;
            timeout = TimeSpan.FromSeconds(Math.Min(value, MaxCallTimeout.TotalSeconds));
        }

        return new McpServerDefinition(name, command, arguments, workingDirectory, environment, description, timeout);
    }

    private static string? Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
}
