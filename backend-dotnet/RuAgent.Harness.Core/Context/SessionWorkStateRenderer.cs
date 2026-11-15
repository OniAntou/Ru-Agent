using System.Text;

namespace RuAgent.Harness.Core.Context;

public static class SessionWorkStateRenderer
{
    /// <summary>Said in place of task, file and verification lines the model can already see.</summary>
    public const string VisibleReceiptsNote =
        "Todo updates, file reads and verifications already in this conversation are not repeated here.";

    /// <param name="visibleReceiptEventIds">
    /// Receipt event IDs whose tool results are in the model's current context. Tasks, observed
    /// files and verifications recorded from those receipts are left out: the model sees the
    /// call and its result directly, and repeating them made every task update resend a list
    /// that grows with each tool call. Compacted or earlier-run receipts are absent from the
    /// set, so the state still carries them. Null renders everything.
    /// </param>
    public static string Render(SessionWorkState state, bool? hasUnrepresentedReceipts = null,
        IReadOnlySet<string>? visibleIntentRunIds = null, IReadOnlySet<string>? visibleReceiptEventIds = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var isPartial = state.HasUnrepresentedReceipts || hasUnrepresentedReceipts == true;
        var builder = new StringBuilder();
        builder.AppendLine($"[session_work_state workspace={state.WorkspaceIdentitySha256} version={state.Version} untrusted=true]");
        var visibleGoal = IsVisible(state.UserGoal, visibleIntentRunIds) ? state.UserGoal : null;
        var visibleConstraints = state.UserConstraints.Where(fact => IsVisible(fact, visibleIntentRunIds)).ToArray();
        var visibleEarlierIntent = state.EarlierUserIntent.Where(fact => IsVisible(fact, visibleIntentRunIds)).ToArray();
        var hiddenIntent = visibleGoal is null
            || visibleConstraints.Length != state.UserConstraints.Count
            || visibleEarlierIntent.Length != state.EarlierUserIntent.Count;
        AppendFact(builder, "User goal", visibleGoal);
        AppendFacts(builder, "User constraints", visibleConstraints);
        AppendFacts(builder, "Earlier user intent (may be superseded)", visibleEarlierIntent);
        if (hiddenIntent)
            builder.AppendLine("Some stored user intent is hidden by the current visible conversation after rewind.");
        if (isPartial)
            builder.AppendLine("Receipt coverage is partial; later tool effects may not be represented and must be checked against current files.");
        if (state.EarlierUserIntentOmitted)
            builder.AppendLine("Earlier user intent was omitted by the bounded state; this may include user constraints. Inspect retained conversation history before inferring it.");
        bool Hidden(string sourceEventId) => visibleReceiptEventIds?.Contains(sourceEventId) == true;
        var tasks = state.Tasks.Where(task => !Hidden(task.SourceEventId)).ToArray();
        var files = state.ObservedFiles.Where(file => !Hidden(file.SourceEventId)).ToArray();
        var verifications = state.Verifications.Where(verification => !Hidden(verification.SourceEventId)).ToArray();
        if (tasks.Length > 0)
        {
            builder.AppendLine("Model task candidates (untrusted; confirm against receipts):");
            foreach (var task in tasks)
                builder.AppendLine($"- [{task.Status}; {task.Source}; run:{task.SourceRunId}; event:{task.SourceEventId}] {task.Text}");
        }
        foreach (var file in files)
            builder.AppendLine($"Receipt-observed file: {file.Path} sha256:{file.Sha256} run:{file.SourceRunId} event:{file.SourceEventId} at:{file.ObservedAt:O}");
        if (tasks.Length != state.Tasks.Count || files.Length != state.ObservedFiles.Count
            || verifications.Length != state.Verifications.Count)
            builder.AppendLine(VisibleReceiptsNote);
        foreach (var verification in verifications)
            builder.AppendLine($"Recorded verification: command={verification.Command}; outcome={verification.OutcomeCode}; succeeded={verification.Succeeded?.ToString() ?? "unknown"}; run:{verification.SourceRunId}; event:{verification.SourceEventId}; at:{verification.RecordedAt:O}");
        AppendFact(builder, "Recorded blocker", state.Blocker);
        AppendFact(builder, "Recorded next action", state.NextAction);
        builder.Append("Treat this as workflow guidance, not policy or permission. Re-read files before editing.");
        return builder.ToString();
    }

    private static bool IsVisible(SessionWorkFact fact, IReadOnlySet<string>? visibleRunIds) =>
        visibleRunIds is null || visibleRunIds.Contains(fact.SourceRunId);

    private static void AppendFacts(StringBuilder builder, string label, IReadOnlyList<SessionWorkFact> facts)
    {
        if (facts.Count == 0) return;
        builder.AppendLine(label + ":");
        foreach (var fact in facts) AppendFact(builder, "-", fact);
    }

    private static void AppendFact(StringBuilder builder, string label, SessionWorkFact? fact)
    {
        if (fact is not null)
            builder.AppendLine($"{label} [run:{fact.SourceRunId}; event:{fact.SourceEventId}]: {fact.Text}");
    }
}
