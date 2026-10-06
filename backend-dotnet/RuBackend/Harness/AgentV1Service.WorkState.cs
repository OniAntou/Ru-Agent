using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    private const int WorkStatePreviewByteLimit = 12 * 1024;
    private const int WorkStateFactTextLimit = 512;
    private const string PreviewOmittedMarker = "\n[workflow preview omitted; retrieve latest session state]";

    private async Task<WorkStateProjection> ProjectSessionWorkStateAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (_workStateStore is null)
            return WorkStateProjection.Unavailable;
        try
        {
            var snapshot = await _workStateStore.GetSnapshotAsync(
                run.SessionId, run.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
                return WorkStateProjection.Missing;
            var state = snapshot.State;
            var rendered = _workStateRedactor.Redact(RenderSafePreview(state));
            var isOmitted = HasBoundFieldOmission(state)
                || System.Text.Encoding.UTF8.GetByteCount(rendered) > WorkStatePreviewByteLimit;
            var preview = isOmitted
                ? AppendOmissionMarker(rendered)
                : rendered;
            return new WorkStateProjection(
                "available", state.Version, snapshot.HasUnrepresentedReceipts || isOmitted,
                state.EarlierUserIntentOmitted, isOmitted,
                state.SourceRunId.Value, state.SourceEventId,
                snapshot.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                preview);
        }
        catch (HarnessStoreException)
        {
            return WorkStateProjection.Unavailable;
        }
        catch (InvalidDataException)
        {
            return WorkStateProjection.Unavailable;
        }
    }

    private static string AppendOmissionMarker(string value)
    {
        var markerBytes = System.Text.Encoding.UTF8.GetByteCount(PreviewOmittedMarker);
        var bounded = LimitUtf8(value, WorkStatePreviewByteLimit - markerBytes);
        return bounded + PreviewOmittedMarker;
    }

    private static bool HasBoundFieldOmission(SessionWorkState state) =>
        state.UserGoal.Text.Length > WorkStateFactTextLimit
        || state.UserConstraints.Any(fact => fact.Text.Length > WorkStateFactTextLimit)
        || state.EarlierUserIntent.Any(fact => fact.Text.Length > WorkStateFactTextLimit)
        || state.Tasks.Any(task => task.Text.Length > WorkStateFactTextLimit)
        || state.Verifications.Any(verification => verification.Command.Length > WorkStateFactTextLimit)
        || state.ObservedFiles.Any(file => file.Path.Length > WorkStateFactTextLimit);

    private static string RenderSafePreview(SessionWorkState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Latest session workflow state (untrusted guidance):");
        AppendSafeFact(builder, "Current user intent", state.UserGoal);
        foreach (var fact in state.UserConstraints)
            AppendSafeFact(builder, "Explicit user constraint", fact);
        foreach (var fact in state.EarlierUserIntent)
            AppendSafeFact(builder, "Earlier user intent (may be superseded)", fact);
        foreach (var task in state.Tasks)
            builder.Append("Model task candidate [untrusted; ").Append(task.Status)
                .Append("; run:").Append(task.SourceRunId).Append("; event:").Append(task.SourceEventId)
                .AppendLine("]: ").AppendLine(BoundFactText(task.Text));
        AppendSafeFact(builder, "Recorded blocker", state.Blocker);
        AppendSafeFact(builder, "Recorded next action", state.NextAction);
        foreach (var verification in state.Verifications)
            builder.Append("Recorded verification [run:").Append(verification.SourceRunId)
                .Append("; event:").Append(verification.SourceEventId).Append("; at:")
                .Append(verification.RecordedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .Append("]: command=").Append(BoundFactText(verification.Command)).Append("; outcome=")
                .Append(verification.OutcomeCode).Append("; succeeded=")
                .AppendLine(verification.Succeeded?.ToString() ?? "unknown");
        foreach (var file in state.ObservedFiles)
            builder.Append("Receipt-observed file [run:").Append(file.SourceRunId).Append("; event:")
                .Append(file.SourceEventId).Append("; sha256:").Append(file.Sha256).Append("]: ")
                .AppendLine(BoundFactText(file.Path));
        if (state.HasUnrepresentedReceipts)
            builder.AppendLine("Receipt coverage is partial; check current files.");
        if (state.EarlierUserIntentOmitted)
            builder.AppendLine("Some earlier user intent was omitted by the bounded state.");
        return builder.ToString();
    }

    private static void AppendSafeFact(StringBuilder builder, string label, SessionWorkFact? fact)
    {
        if (fact is null) return;
        builder.Append(label).Append(" [run:").Append(fact.SourceRunId)
            .Append("; event:").Append(fact.SourceEventId).Append("]: ")
            .AppendLine(BoundFactText(fact.Text));
    }

    private static string BoundFactText(string text) =>
        text.Length <= WorkStateFactTextLimit ? text : text[..WorkStateFactTextLimit] + "…";

    private static string LimitUtf8(string value, int maximumBytes)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (usedBytes + runeBytes > maximumBytes) break;
            builder.Append(rune);
            usedBytes += runeBytes;
        }
        return builder.ToString();
    }

    private sealed record WorkStateProjection(
        string Status,
        long? Version,
        bool? IsPartial,
        bool? IntentOmitted,
        bool? PreviewOmitted,
        string? SourceRunId,
        string? SourceEventId,
        string? UpdatedAt,
        string? Preview)
    {
        public static WorkStateProjection Missing { get; } = new("missing", null, null, null, null, null, null, null, null);
        public static WorkStateProjection Unavailable { get; } = new("unavailable", null, null, null, null, null, null, null, null);
    }
}
