namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// A small, explicit prompt variation selected from run-pinned provider metadata.
/// Unknown or custom routes use <see cref="Neutral"/>.
/// </summary>
public enum PromptRecipe
{
    Neutral,
    CodexNative,
}

internal static class PromptRecipeText
{
    public static string For(PromptRecipe recipe) => recipe switch
    {
        PromptRecipe.Neutral => string.Empty,
        PromptRecipe.CodexNative => "For this coding workflow, keep narration concise and lead with the outcome; include only evidence needed to support it and state unresolved uncertainty.",
        _ => throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "Unknown prompt recipe."),
    };
}
