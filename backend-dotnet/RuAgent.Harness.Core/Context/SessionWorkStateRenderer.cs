using System.Text;

namespace RuAgent.Harness.Core.Context;

public static class SessionWorkStateRenderer
{
    public static string Render(SessionWorkState state, bool? hasUnrepresentedReceipts = null,
        IReadOnlySet<string>? visibleIntentRunIds = null)
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
        if (state.Tasks.Count > 0)
        {
            builder.AppendLine("Model task candidates (untrusted; confirm against receipts):");
            foreach (var task in state.Tasks)
                builder.AppendLine($"- [{task.Status}; {task.Source}; run:{task.SourceRunId}; event:{task.SourceEventId}] {task.Text}");
        }
        foreach (var file in state.ObservedFiles)
            builder.AppendLine($"Receipt-observed file: {file.Path} sha256:{file.Sha256} run:{file.SourceRunId} event:{file.SourceEventId} at:{file.ObservedAt:O}");
        foreach (var verification in state.Verifications)
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
