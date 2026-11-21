using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Providers;

namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// Bounded recovery for a step whose stored input cannot become a valid provider
/// conversation: an unpaired batch, a reused call id, a missing prompt, or a
/// history/request above its ceiling. Stored input is Host data, so it must not
/// become a terminal run failure the user cannot act on. The repaired projection
/// keeps every complete batch; when even that cannot be built, the text-only
/// fallback keeps the dialogue and drops only tool-call structure.
/// </summary>
public sealed partial class InProcessModelStepRunner
{
    private static bool IsRecoverableStepInputFailure(string code) =>
        code is "model_history_invalid" or "provider_continuation_invalid" or "provider_continuation_missing"
            or "model_history_too_large" or "provider_continuation_too_large"
            or "request_too_large" or "invalid_request" or "missing_user_prompt";

    /// <summary>
    /// The same bounded code can arrive from the Host's own conversation builder
    /// (<see cref="ModelRunnerProtocolException"/>) or from the provider-continuation
    /// reader (<see cref="ProviderCatalogException"/>), so recovery must recognize both.
    /// </summary>
    private static string? RecoverableStepInputCode(Exception error) => error switch
    {
        ModelRunnerProtocolException runner when IsRecoverableStepInputFailure(runner.Code) => runner.Code,
        ProviderCatalogException catalog when IsRecoverableStepInputFailure(catalog.Code) => catalog.Code,
        _ => null,
    };

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

    private const string HistoryRepairNote =
        "[host recovery note] Earlier tool calls and their results could not be replayed and were "
        + "left out of this request. Check the current workspace state before repeating an action.";

    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversation(
        ModelContextSnapshot context, bool hasImages, string errorCode, bool forceStructureFree = false)
    {
        // A provider-continuation failure means replayed reasoning cannot be matched;
        // keeping assistant tool calls without their replay is the unsafe shape, so
        // those fall straight through to the structure-free form. A second recovery
        // pass must also reduce structure differently from the first failure.
        if (!forceStructureFree
            && errorCode is not ("provider_continuation_invalid" or "provider_continuation_missing"
                or "provider_continuation_too_large"))
        {
            try
            {
                var sanitized = ModelHistoryRepair.Sanitize(context.Entries);
                // A ceiling failure that dropped nothing is the same size after repair;
                // go straight to the structure-free form instead of failing it twice.
                var sizeFailure = errorCode is "request_too_large" or "model_history_too_large";
                if (errorCode != "missing_user_prompt" && !(sizeFailure && !sanitized.DroppedAny))
                {
                    var reparsed = new ModelContextSnapshot(sanitized.Entries, ReadOnlyMemory<byte>.Empty);
                    var (prompt, _, messages) = BuildConversation(reparsed, hasImages);
                    return (prompt, messages!);
                }
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
        ModelContextSnapshot context, bool hasImages, string errorCode, bool forceStructureFree = false)
    {
        try
        {
            return RecoverConversation(context, hasImages, errorCode, forceStructureFree);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return (hasImages ? string.Empty : "Continue.", Array.Empty<ModelTurn>());
        }
    }

    internal static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversationForTests(
        ModelContextSnapshot context, bool hasImages = false) =>
        RecoverConversationOrLastResort(context, hasImages, "model_history_invalid");

    internal static (string Prompt, IReadOnlyList<ModelTurn> Messages) RecoverConversationForCodeForTests(
        ModelContextSnapshot context, string errorCode, bool hasImages = false, bool forceStructureFree = false) =>
        RecoverConversationOrLastResort(context, hasImages, errorCode, forceStructureFree);

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
                    units.Add([new ModelTurn("system", system)]);
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

        // The structure below is gone; say so once, immediately before the request.
        units.Add([new ModelTurn("user", HistoryRepairNote)]);

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
