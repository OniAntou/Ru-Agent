namespace RuAgent.Protocol;

/// <summary>
/// The one list of directories Markdown skills are discovered in. The prompt listing, the
/// <c>skill</c> tool and the <c>/skills</c> command all read it, so a skill that is advertised
/// to the model can always be loaded and listed. Earlier each of them kept its own list and
/// they disagreed about the environment override and the per-user folders.
/// </summary>
/// <remarks>
/// Order is precedence: when two roots hold a skill with the same id the first one wins, so a
/// workspace skill overrides a user-wide one. Roots that do not exist are skipped by the loader.
/// </remarks>
public static class SkillRoots
{
    public const string ConfiguredRootVariable = "RU_SKILLS_ROOT";

    public static IReadOnlyList<string> For(
        string workspaceRoot,
        string? localApplicationDataRoot = null,
        string? userProfileRoot = null,
        string? configuredRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var roots = new List<string>
        {
            Path.Combine(workspaceRoot, "skills"),
            Path.Combine(workspaceRoot, ".ru", "skills"),
            Path.Combine(workspaceRoot, ".agents", "skills"),
        };

        configuredRoot ??= Environment.GetEnvironmentVariable(ConfiguredRootVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            roots.Add(configuredRoot);
        }

        userProfileRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfileRoot))
        {
            roots.Add(Path.Combine(userProfileRoot, ".ru", "skills"));
        }

        localApplicationDataRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationDataRoot))
        {
            roots.Add(Path.Combine(localApplicationDataRoot, "Ru", "skills"));
        }

        return roots;
    }
}
