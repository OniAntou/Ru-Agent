using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// Bounded recovery for a step whose stored history cannot be projected as a valid
/// provider conversation. Stored history is Host input, so a single unpaired batch
/// must not become a terminal run failure the user cannot act on. The sanitized
/// projection keeps every complete batch; when even that cannot be projected, the
/// text-only fallback keeps the dialogue and drops only tool-call structure.
/// </summary>
public sealed partial class InProcessModelStepRunner
{
    private static bool IsRecoverableHistoryFailure(string code) =>
        code is "model_history_invalid" or "provider_continuation_invalid" or "provider_continuation_missing";

    private void ReportHistoryRepair(string code)
    {
        try
        {
            ObserveHistoryRepair?.Invoke(code);
        }
        catch
        {
            // A diagnostics sink must never turn a repaired step into a failed one.
        }
    }

    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversation(
        ModelContextSnapshot context, bool hasImages, string errorCode)
    {
        if (errorCode == "model_history_invalid")
        {
            try
            {
                var sanitized = ModelHistoryRepair.Sanitize(context.Entries);
                var reparsed = new ModelContextSnapshot(sanitized, ReadOnlyMemory<byte>.Empty);
                var (prompt, _, messages) = BuildConversation(reparsed, hasImages);
                return (prompt, messages!);
            }
            catch (Exception error) when (error is ModelRunnerProtocolException
                or ArgumentException or InvalidOperationException)
            {
                // The repaired projection still cannot be built; the text-only form below
                // is smaller and cannot carry an unpaired call.
            }
        }

        return BuildTextOnlyConversation(context.Entries, hasImages);
    }

    /// <summary>
    /// The recovery entry point used by one step: the repaired projection, the text-only
    /// form, and finally a plain continuation prompt. A stored history must never stop
    /// the run, even if a future repair path itself throws.
    /// </summary>
    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversationOrLastResort(
        ModelContextSnapshot context, bool hasImages, string errorCode)
    {
        try
        {
            return RecoverConversation(context, hasImages, errorCode);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return (hasImages ? string.Empty : "Continue.", Array.Empty<ModelTurn>());
        }
    }

    internal static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversationForTests(
        ModelContextSnapshot context, bool hasImages = false) =>
        RecoverConversationOrLastResort(context, hasImages, "model_history_invalid");

    internal static (string Prompt, IReadOnlyList<ModelTurn> Messages) BuildTextOnlyConversationForTests(
        IReadOnlyList<ModelContextEntry> entries, bool hasImages = false) =>
        BuildTextOnlyConversation(entries, hasImages);

    /// <summary>
    /// A conversation with no tool calls at all: system notes, user text, summaries and
    /// assistant prose. It is the last bounded fallback, so it must not depend on any
    /// stored pairing. The newest user text stays the request prompt.
    /// </summary>
    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) BuildTextOnlyConversation(
        IReadOnlyList<ModelContextEntry> entries,
        bool hasImages)
    {
        var systems = new List<ModelTurn>();
        var units = new List<List<ModelTurn>>();
        var userBlocks = new List<string>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.System)
            {
                FlushUserUnits(units, userBlocks);
                if (ConversationText(entry.Content) is { } system)
                {
                    systems.Add(new ModelTurn("system", system));
                }

                continue;
            }

            if (entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.User)
            {
                if (RuntimeContextSnapshot.IsSnapshot(entry))
                {
                    continue;
                }

                if (ConversationText(entry.Content) is not { } user)
                {
                    continue;
                }

                userBlocks.Add(user);
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.Summary && ConversationText(entry.Content) is { } summary)
            {
                FlushUserUnits(units, userBlocks);
                units.Add([new ModelTurn("user", $"compacted history summary:\n{summary}")]);
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.AssistantStep
                && entry.AssistantStep is { ToolCalls.Count: 0 } step
                && ConversationText(step.Text) is { } text)
            {
                FlushUserUnits(units, userBlocks);
                units.Add([new ModelTurn("assistant", text)]);
            }
        }

        for (var index = 0; index + 1 < userBlocks.Count; index++)
        {
            units.Add([new ModelTurn("user", userBlocks[index])]);
        }

        if (systems.Count > 0)
        {
            units.InsertRange(0, systems.Select(turn => new List<ModelTurn> { turn }));
        }

        var messages = FitModelMessagesToBudget(units);
        var prompt = userBlocks.Count > 0
            ? userBlocks[^1]
            : hasImages ? string.Empty : "Continue.";
        return (prompt, messages);
    }

    private static void FlushUserUnits(List<List<ModelTurn>> units, List<string> blocks)
    {
        foreach (var block in blocks)
        {
            units.Add([new ModelTurn("user", block)]);
        }

        blocks.Clear();
    }

    private static string? ConversationText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var withoutNul = content.Replace("\0", string.Empty);
        return string.IsNullOrWhiteSpace(withoutNul)
            ? null
            : ClampConversationBlock(withoutNul, MaxConversationBlockChars);
    }
}
