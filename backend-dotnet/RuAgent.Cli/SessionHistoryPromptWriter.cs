namespace RuAgent.Cli;

/// <summary>
/// Writes one prompt read back from session history.
/// </summary>
/// <remarks>
/// A reopened prompt is the same user block the composer echoed live: the terminal draws
/// it as a styled block, the line/JSON adapters keep the plain <c>&gt;</c> echo. Writing it
/// through the plain text sink in both cases left history unboxed, so it read as prose
/// where the live turn read as something the operator had typed.
/// <para>
/// The decision lives here rather than inline in the session because that path is a local
/// function inside a session that needs real Host clients to build, which left the choice
/// untestable.
/// </para>
/// </remarks>
internal static class SessionHistoryPromptWriter
{
    /// <param name="prompt">The stored prompt, possibly wrapping inlined attachments.</param>
    /// <param name="tgMode">Whether the terminal surface, rather than a text adapter, owns output.</param>
    /// <param name="echoPrompt">Draws the composer's user block for the typed text.</param>
    /// <param name="writeLine">Writes one plain line.</param>
    internal static void Write(
        string prompt,
        bool tgMode,
        Action<string> echoPrompt,
        Action<string> writeLine)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(echoPrompt);
        ArgumentNullException.ThrowIfNull(writeLine);

        var (typed, labels) = Tui.ChatPromptEcho.SplitStored(prompt);
        if (tgMode)
        {
            echoPrompt(typed);
            foreach (var label in labels)
            {
                writeLine("  " + label);
            }

            return;
        }

        writeLine(Tui.ChatPromptEcho.Format(typed, labels.Count == 0 ? null : labels));
    }
}
