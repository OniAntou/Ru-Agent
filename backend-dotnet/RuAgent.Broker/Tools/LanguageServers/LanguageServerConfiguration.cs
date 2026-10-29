using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.LanguageServers;

public sealed record LanguageServerDefinition(string Language, string Command, IReadOnlyList<string> Arguments);

/// <summary>Explicit programs from the user's profile; workspace configuration is never loaded.</summary>
public sealed class LanguageServerConfiguration(IReadOnlyDictionary<string, LanguageServerDefinition> servers)
{
    public const int MaximumBytes = 32 * 1024;
    public const int MaximumServers = 2;
    public const int MaximumArguments = 32;
    public const int MaximumArgumentLength = 2048;
    private static readonly Regex CommandPattern = new("^[^\\0\\r\\n]{1,1024}$", RegexOptions.CultureInvariant);
    private static readonly Lazy<LanguageServerConfiguration> UserConfiguration = new(
        () => Load(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), LazyThreadSafetyMode.ExecutionAndPublication);
    public IReadOnlyDictionary<string, LanguageServerDefinition> Servers { get; } = servers;
    public LanguageServerDefinition? Find(string language) => Servers.GetValueOrDefault(language);
    public static string PathFor(string home) => Path.Combine(home, ".ru", "lsp.json");
    public static LanguageServerConfiguration Empty { get; } = new(new Dictionary<string, LanguageServerDefinition>(StringComparer.Ordinal));
    public static LanguageServerConfiguration CurrentUser => UserConfiguration.Value;

    public static LanguageServerConfiguration Load(string? userHome)
    {
        if (string.IsNullOrWhiteSpace(userHome)) return Empty;
        try
        {
            var path = PathFor(userHome);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return Empty;
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Empty; }
    }

    internal static LanguageServerConfiguration Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Object || servers.EnumerateObject().Count() > MaximumServers) return Empty;
        var result = new Dictionary<string, LanguageServerDefinition>(StringComparer.Ordinal);
        foreach (var item in servers.EnumerateObject())
        {
            if (item.Name is not ("csharp" or "typescript") || result.ContainsKey(item.Name)
                || item.Value.ValueKind != JsonValueKind.Object
                || !item.Value.TryGetProperty("command", out var commandNode) || commandNode.ValueKind != JsonValueKind.String) return Empty;
            var command = commandNode.GetString();
            if (command is null || !CommandPattern.IsMatch(command) || !item.Value.TryGetProperty("args", out var argsNode)
                || argsNode.ValueKind != JsonValueKind.Array || argsNode.GetArrayLength() > MaximumArguments) return Empty;
            var args = new List<string>();
            foreach (var argNode in argsNode.EnumerateArray())
            {
                if (argNode.ValueKind != JsonValueKind.String) return Empty;
                var arg = argNode.GetString();
                if (arg is null || arg.Length > MaximumArgumentLength || arg.Contains('\0')) return Empty;
                args.Add(arg);
            }
            result.Add(item.Name, new LanguageServerDefinition(item.Name, command, args));
        }
        return new LanguageServerConfiguration(result);
    }
}
