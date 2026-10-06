namespace RuAgent.Harness.Core.Context;

public static class SessionWorkStateReducer
{
    public static SessionWorkState StartRun(
        SessionWorkState? previous,
        SessionId sessionId,
        WorkspaceId workspaceId,
        string workspaceIdentitySha256,
        RunId sourceRunId,
        EventId sourceEventId,
        string explicitUserPrompt)
    {
        var earlierIntent = previous is null
            ? Array.Empty<SessionWorkFact>()
            : PreserveFirstIntent(previous.EarlierUserIntent.Append(previous.UserGoal).ToArray());
        var hasOmitted = (previous?.EarlierUserIntentOmitted ?? false)
            || (previous is not null
                && previous.EarlierUserIntent.Count >= SessionWorkState.MaximumItems);
        while (true)
        {
            try
            {
                return new SessionWorkState(
                    sessionId, workspaceId, workspaceIdentitySha256, sourceRunId,
                    (previous?.Version ?? 0) + 1, sourceEventId.Value,
                    new SessionWorkFact(explicitUserPrompt, sourceRunId.Value, sourceEventId.Value),
                    previous?.UserConstraints ?? Array.Empty<SessionWorkFact>(),
                    earlierIntent,
                    hasOmitted,
                    previous?.HasUnrepresentedReceipts ?? false,
                    previous?.Tasks ?? Array.Empty<SessionWorkItem>(),
                    previous?.ObservedFiles ?? Array.Empty<SessionObservedFile>(),
                    previous?.Verifications ?? Array.Empty<SessionVerificationRecord>(),
                    previous?.Blocker,
                    previous?.NextAction);
            }
            catch (ArgumentException error) when (IsAggregateCap(error) && earlierIntent.Length > 1)
            {
                earlierIntent = OmitOldestNonAnchor(earlierIntent);
                hasOmitted = true;
            }
        }
    }

    private static SessionWorkFact[] PreserveFirstIntent(SessionWorkFact[] facts)
    {
        if (facts.Length <= SessionWorkState.MaximumItems) return facts;
        return [facts[0], .. facts.TakeLast(SessionWorkState.MaximumItems - 1)];
    }

    private static SessionWorkFact[] OmitOldestNonAnchor(SessionWorkFact[] facts) =>
        facts.Length <= 1 ? facts : [facts[0], .. facts.Skip(2)];

    private static bool IsAggregateCap(ArgumentException error) =>
        error.Message.Contains("serialized cap", StringComparison.Ordinal);
}
