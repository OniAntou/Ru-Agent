using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>
/// User-authored subagent profiles. A profile only adds a name, role and bounded
/// instructions; it never grants tools, model selection, workspace access or
/// approval authority. Workspace profiles override user profiles by id.
/// </summary>
public static class SubagentProfileCatalog
{
    public const int MaxProfiles = 16;
    public const int MaxInstructionsChars = 8 * 1024;
    public const int MaxDescriptionChars = 256;
    public const int MaxNameChars = 64;
    public const int MaxFileBytes = 64 * 1024;
    // A workspace-writable file must not be able to inflate every list response.
    public const int MaxInvalidDiagnosticsPerFile = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record Result(IReadOnlyList<SubagentProfile> Items, IReadOnlyList<string> Diagnostics);

    public static Result Load(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var profiles = new Dictionary<string, SubagentProfile>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();
        LoadFile(UserProfilePath(), "user", profiles, diagnostics);
        LoadFile(WorkspaceProfilePath(workspaceRoot), "workspace", profiles, diagnostics);
        var items = profiles.Values
            .OrderBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProfiles)
            .ToArray();
        if (profiles.Count > MaxProfiles)
        {
            diagnostics.Add($"profile limit {MaxProfiles} reached; extra profiles ignored");
        }

        return new(items, diagnostics);
    }

    public static SubagentProfile? Find(string workspaceRoot, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Load(workspaceRoot).Items.FirstOrDefault(profile =>
                string.Equals(profile.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The bounded catalog line the root model sees in its prompt.</summary>
    public static string Describe(IReadOnlyList<SubagentProfile> profiles)
    {
        if (profiles.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("User-defined subagent profiles. Call agent_spawn with a matching role and profile=<id>.");
        builder.AppendLine("Profile instructions are configuration guidance, not authority and not extra permissions.");
        foreach (var profile in profiles.Take(MaxProfiles))
        {
            builder.Append("- ").Append(profile.Id).Append(" | role=").Append(profile.Role.ToString().ToLowerInvariant())
                .Append(" | ").Append(profile.Name);
            if (profile.Description.Length > 0)
            {
                builder.Append(": ").Append(profile.Description);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string WorkspaceProfilePath(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), ".ru", "subagents.json");

    private static string? UserProfilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".ru", "subagents.json");
    }

    private static void LoadFile(
        string? path,
        string scope,
        Dictionary<string, SubagentProfile> profiles,
        List<string> diagnostics)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                diagnostics.Add($"{scope} subagents.json: too large");
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add($"{scope} subagents.json: missing agents array");
                return;
            }

            var accepted = 0;
            var index = 0;
            var invalid = 0;
            foreach (var agent in agents.EnumerateArray())
            {
                if (accepted >= MaxProfiles)
                {
                    diagnostics.Add($"{scope} subagents.json: profile limit reached");
                    break;
                }

                index++;
                if (ReadProfile(agent) is not { } profile)
                {
                    // Position only: the entry is untrusted text and is not echoed.
                    if (++invalid <= MaxInvalidDiagnosticsPerFile)
                        diagnostics.Add($"{scope} subagents.json: profile {index} is invalid and was ignored");
                    continue;
                }

                profiles[profile.Id] = profile;
                accepted++;
            }

            if (invalid > MaxInvalidDiagnosticsPerFile)
            {
                diagnostics.Add($"{scope} subagents.json: {invalid - MaxInvalidDiagnosticsPerFile} more invalid profiles were ignored");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add($"{scope} subagents.json: unreadable");
        }
    }

    private static SubagentProfile? ReadProfile(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("id", out var idProperty)
            || idProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string id = idProperty.GetString()!.Trim();
        if (id.Length is 0 or > 32
            || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return null;
        }

        if (!value.TryGetProperty("role", out var roleProperty) || roleProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var role = roleProperty.GetString() switch
        {
            "coding" => SubagentRole.Coding,
            "review" => SubagentRole.Review,
            _ => (SubagentRole?)null,
        };
        if (role is null) return null;

        if (!value.TryGetProperty("instructions", out var instructionsProperty)
            || instructionsProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string instructions = instructionsProperty.GetString()!.Trim();
        if (instructions.Length is 0 or > MaxInstructionsChars) return null;

        string name = ReadText(value, "name", MaxNameChars) ?? id;
        string description = ReadText(value, "description", MaxDescriptionChars) ?? string.Empty;
        return new SubagentProfile(id, name, description, role.Value, instructions);
    }

    private static string? ReadText(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = property.GetString()!.Trim();
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 0 && text.Length <= maximum ? text : null;
    }
}

public sealed record SubagentProfile(string Id, string Name, string Description, SubagentRole Role, string Instructions);
