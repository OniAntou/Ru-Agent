namespace RuApp.Services;

/// <summary>
/// Copy for the workspace trust gate, split by first trust versus re-trust.
/// </summary>
/// <remarks>
/// A moved, recreated, or changed root presents a different canonical identity
/// at a path the user already approved. The gate stays one explicit click
/// either way; the re-trust copy exists so the second click is not a mystery
/// and names what changed. The decision itself is always the Host's.
/// </remarks>
public static class WorkspaceTrustPromptPolicy
{
    public sealed record Prompt(string Title, string Content, string PrimaryButtonText);

    public static Prompt For(bool isReTrust) => isReTrust
        ? new Prompt(
            "Trust this workspace again?",
            "This folder was trusted before, but its identity changed since — it was moved, recreated, or its root files changed. "
                + "Approve to trust the new identity; the earlier trust does not cover it.",
            "Trust again")
        : new Prompt(
            "Trust this workspace?",
            "This run needs explicit workspace trust before it can start. Approve only if you trust this workspace and the Coding Agent.",
            "Approve");
}
