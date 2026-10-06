using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Harness.Core.Context;

public enum SessionWorkItemStatus { Open, Completed, Unknown }
public enum SessionWorkItemSource { User, Model, Receipt }

public sealed record SessionWorkFact(string Text, string SourceRunId, string SourceEventId);
public sealed record SessionWorkItem(
    string Text,
    SessionWorkItemStatus Status,
    SessionWorkItemSource Source,
    string SourceRunId,
    string SourceEventId);
public sealed record SessionObservedFile(
    string Path,
    string Sha256,
    string SourceRunId,
    string SourceEventId,
    DateTimeOffset ObservedAt);
public sealed record SessionVerificationRecord(
    string Command,
    string OutcomeCode,
    bool? Succeeded,
    string SourceRunId,
    string SourceEventId,
    DateTimeOffset RecordedAt);

public sealed record SessionWorkState
{
    public const string ContextSourceId = "base:session-work-state";
    public const int MaximumUtf8Bytes = 16 * 1024;
    public const int MaximumItems = 16;

    public SessionWorkState(
        SessionId sessionId,
        WorkspaceId workspaceId,
        string workspaceIdentitySha256,
        RunId sourceRunId,
        long version,
        string sourceEventId,
        SessionWorkFact userGoal,
        IReadOnlyList<SessionWorkFact> userConstraints,
        IReadOnlyList<SessionWorkFact> earlierUserIntent,
        bool earlierUserIntentOmitted,
        bool hasUnrepresentedReceipts,
        IReadOnlyList<SessionWorkItem> tasks,
        IReadOnlyList<SessionObservedFile> observedFiles,
        IReadOnlyList<SessionVerificationRecord> verifications,
        SessionWorkFact? blocker,
        SessionWorkFact? nextAction)
    {
        _ = sessionId.Value;
        _ = workspaceId.Value;
        _ = sourceRunId.Value;
        _ = new EventId(sourceEventId).Value;
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        SessionId = sessionId;
        WorkspaceId = workspaceId;
        WorkspaceIdentitySha256 = RequireHash(workspaceIdentitySha256, nameof(workspaceIdentitySha256));
        SourceRunId = sourceRunId;
        Version = version;
        SourceEventId = sourceEventId;
        UserGoal = CopyFact(userGoal, nameof(userGoal));
        UserConstraints = CopyFacts(userConstraints, nameof(userConstraints));
        EarlierUserIntent = CopyFacts(earlierUserIntent, nameof(earlierUserIntent));
        EarlierUserIntentOmitted = earlierUserIntentOmitted;
        HasUnrepresentedReceipts = hasUnrepresentedReceipts;
        Tasks = CopyTasks(tasks);
        ObservedFiles = CopyObservedFiles(observedFiles);
        Verifications = CopyVerifications(verifications);
        Blocker = blocker is null ? null : CopyFact(blocker, nameof(blocker));
        NextAction = nextAction is null ? null : CopyFact(nextAction, nameof(nextAction));
        _ = SessionWorkStateCodec.Serialize(this);
    }

    public SessionId SessionId { get; }
    public WorkspaceId WorkspaceId { get; }
    public string WorkspaceIdentitySha256 { get; }
    public RunId SourceRunId { get; }
    public long Version { get; }
    public string SourceEventId { get; }
    public SessionWorkFact UserGoal { get; }
    public IReadOnlyList<SessionWorkFact> UserConstraints { get; }
    public IReadOnlyList<SessionWorkFact> EarlierUserIntent { get; }
    public bool EarlierUserIntentOmitted { get; }
    public bool HasUnrepresentedReceipts { get; }
    public IReadOnlyList<SessionWorkItem> Tasks { get; }
    public IReadOnlyList<SessionObservedFile> ObservedFiles { get; }
    public IReadOnlyList<SessionVerificationRecord> Verifications { get; }
    public SessionWorkFact? Blocker { get; }
    public SessionWorkFact? NextAction { get; }

    public SessionWorkState WithUnrepresentedReceipts() => HasUnrepresentedReceipts
        ? this
        : new SessionWorkState(SessionId, WorkspaceId, WorkspaceIdentitySha256, SourceRunId, Version,
            SourceEventId, UserGoal, UserConstraints, EarlierUserIntent, EarlierUserIntentOmitted,
            true, Tasks, ObservedFiles, Verifications, Blocker, NextAction);

    private static SessionWorkFact CopyFact(SessionWorkFact fact, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(fact, parameterName);
        _ = RequireText(fact.Text, parameterName);
        _ = new RunId(fact.SourceRunId);
        _ = new EventId(fact.SourceEventId);
        return fact;
    }

    private static IReadOnlyList<SessionWorkFact> CopyFacts(IReadOnlyList<SessionWorkFact> values, string name) =>
        CopyList(values, name, CopyFact);

    private static IReadOnlyList<SessionWorkItem> CopyTasks(IReadOnlyList<SessionWorkItem> values) =>
        CopyList(values, nameof(values), (item, name) =>
        {
            ArgumentNullException.ThrowIfNull(item);
            _ = RequireText(item.Text, name);
            if (!Enum.IsDefined(item.Status) || !Enum.IsDefined(item.Source))
                throw new ArgumentException("Work item metadata is invalid.", name);
            _ = new RunId(item.SourceRunId);
            _ = new EventId(item.SourceEventId);
            return item;
        });

    private static IReadOnlyList<SessionObservedFile> CopyObservedFiles(IReadOnlyList<SessionObservedFile> values) =>
        CopyList(values, nameof(values), (item, name) =>
        {
            ArgumentNullException.ThrowIfNull(item);
            _ = RequireText(item.Path, name);
            _ = RequireHash(item.Sha256, name);
            _ = new RunId(item.SourceRunId);
            _ = new EventId(item.SourceEventId);
            if (item.ObservedAt == default) throw new ArgumentException("File observation time is required.", name);
            return item;
        });

    private static IReadOnlyList<SessionVerificationRecord> CopyVerifications(IReadOnlyList<SessionVerificationRecord> values) =>
        CopyList(values, nameof(values), (item, name) =>
        {
            ArgumentNullException.ThrowIfNull(item);
            _ = RequireText(item.Command, name);
            _ = RequireText(item.OutcomeCode, name);
            _ = new RunId(item.SourceRunId);
            _ = new EventId(item.SourceEventId);
            if (item.RecordedAt == default) throw new ArgumentException("Verification time is required.", name);
            return item;
        });

    private static IReadOnlyList<T> CopyList<T>(IReadOnlyList<T> values, string name, Func<T, string, T> copy)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count > MaximumItems) throw new ArgumentException("Work state list exceeds its cap.", name);
        return new ReadOnlyCollection<T>(values.Select(value => copy(value, name)).ToArray());
    }

    private static string RequireText(string? value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes)
            throw new ArgumentException("Work state text exceeds its cap.", name);
        return value;
    }

    private static string RequireHash(string? value, string name)
    {
        if (value is not { Length: 64 }
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Expected a lowercase SHA-256 digest.", name);
        return value;
    }
}
