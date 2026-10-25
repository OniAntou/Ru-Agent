using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuBackend.Harness.Hooks;

public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    UserPromptSubmit,
    Stop,
}

/// <summary>One user-configured command bound to a tool-id pattern.</summary>
public sealed record HookDefinition(HookEvent Event, string? ToolPattern, string Command, TimeSpan Timeout)
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public bool Matches(string toolId)
    {
        if (string.IsNullOrEmpty(ToolPattern) || ToolPattern == "*") return true;
        try
        {
            return Regex.IsMatch(toolId, "^(?:" + ToolPattern + ")$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Hooks from <c>~/.ru/hooks.json</c> then <c>&lt;workspace&gt;/.ru/hooks.json</c>:
/// <code>{"hooks":{"PreToolUse":[{"matcher":"powershell|run_process|edit_file","command":"...","timeout_seconds":30}]}}</code>
/// Unreadable, oversized or malformed files contribute nothing; individual invalid entries are skipped.
/// </summary>
public sealed class HookConfiguration
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxHooksPerEvent = 32;
    public const int MaxCommandLength = 4096;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(120);

    public static HookConfiguration Empty { get; } = new([]);

    private readonly IReadOnlyList<HookDefinition> _hooks;

    public HookConfiguration(IReadOnlyList<HookDefinition> hooks) => _hooks = hooks;

    public bool IsEmpty => _hooks.Count == 0;

    public IReadOnlyList<HookDefinition> For(HookEvent hookEvent, string toolId) =>
        _hooks.Where(hook => hook.Event == hookEvent && hook.Matches(toolId)).ToArray();

    public bool Has(HookEvent hookEvent) => _hooks.Any(hook => hook.Event == hookEvent);

    /// <summary>Hooks of a run-level event; a tool matcher has nothing to match there and is ignored.</summary>
    public IReadOnlyList<HookDefinition> ForEvent(HookEvent hookEvent) =>
        _hooks.Where(hook => hook.Event == hookEvent).ToArray();

    /// <param name="workspaceRoot">Null loads only the user's hooks, as for a workspace not yet trusted.</param>
    public static HookConfiguration Load(string? workspaceRoot, string? userHome)
    {
        var hooks = new List<HookDefinition>();
        foreach (var root in new[] { userHome, workspaceRoot })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            hooks.AddRange(ReadFile(Path.Combine(root, ".ru", "hooks.json")));
        }

        return hooks.Count == 0 ? Empty : new HookConfiguration(hooks);
    }

    internal static IReadOnlyList<HookDefinition> Parse(string json)
    {
        var result = new List<HookDefinition>();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("hooks", out var hooks)
            || hooks.ValueKind != JsonValueKind.Object) return result;
        foreach (var hookEvent in Enum.GetValues<HookEvent>())
        {
            if (!hooks.TryGetProperty(hookEvent.ToString(), out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray().Take(MaxHooksPerEvent))
            {
                if (TryReadEntry(hookEvent, entry) is { } hook) result.Add(hook);
            }
        }

        return result;
    }

    private static HookDefinition? TryReadEntry(HookEvent hookEvent, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) return null;
        var text = command.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCommandLength || text.Contains('\0')) return null;
        string? pattern = null;
        if (entry.TryGetProperty("matcher", out var matcher))
        {
            if (matcher.ValueKind != JsonValueKind.String) return null;
            pattern = matcher.GetString();
        }

        var timeout = DefaultTimeout;
        if (entry.TryGetProperty("timeout_seconds", out var seconds))
        {
            if (!seconds.TryGetInt32(out var value) || value < 1) return null;
            timeout = TimeSpan.FromSeconds(Math.Min(value, MaxTimeout.TotalSeconds));
        }

        return new HookDefinition(hookEvent, pattern, text, timeout);
    }

    private static IReadOnlyList<HookDefinition> ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return [];
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
