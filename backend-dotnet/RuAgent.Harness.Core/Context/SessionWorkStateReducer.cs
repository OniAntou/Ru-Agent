namespace RuAgent.Harness.Core.Context;

public static class SessionWorkStateReducer
{
    private static readonly char[] ConstraintBoundaries = ['\r', '\n', ';'];

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
        var constraints = PreserveFirstAndRecent((previous?.UserConstraints ?? Array.Empty<SessionWorkFact>())
            .Concat(ReadExplicitConstraints(explicitUserPrompt, sourceRunId, sourceEventId)).ToArray(), out var constraintsOmitted);
        var hasOmitted = constraintsOmitted || (previous?.EarlierUserIntentOmitted ?? false)
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
                    constraints,
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
            catch (ArgumentException error) when (IsAggregateCap(error) && constraints.Length > 0)
            {
                constraints = OmitOldestConstraint(constraints);
                hasOmitted = true;
            }
        }
    }

    // This lexical screen intentionally captures only clear imperative forms; it is not
    // a semantic extractor for every constraint expressed in a user prompt.
    private static IEnumerable<SessionWorkFact> ReadExplicitConstraints(string prompt, RunId runId, EventId eventId)
    {
        foreach (var raw in SplitConstraintCandidates(prompt))
        {
            var candidate = raw.Trim().TrimStart('-', '*', '•', ' ', '\t').Trim();
            if (candidate.Length == 0 || !LooksLikeExplicitConstraint(candidate)) continue;
            yield return new SessionWorkFact(candidate, runId.Value, eventId.Value);
        }
    }

    private static IEnumerable<string> SplitConstraintCandidates(string prompt)
    {
        var start = 0;
        for (var index = 0; index < prompt.Length; index++)
        {
            var punctuation = prompt[index] is '.' or '!' or '?';
            var boundary = ConstraintBoundaries.Contains(prompt[index])
                || punctuation && (index + 1 == prompt.Length || char.IsWhiteSpace(prompt[index + 1]));
            if (!boundary) continue;
            if (index >= start) yield return prompt[start..(index + 1)];
            start = index + 1;
        }
        if (start < prompt.Length) yield return prompt[start..];
    }

    private static bool LooksLikeExplicitConstraint(string text)
    {
        var probe = text;
        foreach (var label in new[] { "correction:", "actually:", "critical:", "important:", "constraint:", "sửa lại:", "đính chính:", "lưu ý:" })
        {
            if (probe.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                probe = probe[label.Length..].TrimStart();
                break;
            }
        }
        return StartsWithAny(probe,
            "keep ", "preserve ", "do not ", "don't ", "never ", "avoid ", "retain ", "ensure ", "make sure ",
            "must ", "must not ", "should ", "always ", "please keep ", "please preserve ", "please do not ", "please don't ",
            "đừng ", "không được ", "phải ", "giữ ", "hãy ", "tránh ", "đảm bảo ", "nhớ ", "luôn ");
    }

    private static bool StartsWithAny(string text, params string[] prefixes) =>
        prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static SessionWorkFact[] PreserveFirstAndRecent(SessionWorkFact[] facts, out bool omitted)
    {
        var latest = new List<SessionWorkFact>(facts.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = facts.Length - 1; index >= 0; index--)
        {
            if (seen.Add(facts[index].Text)) latest.Add(facts[index]);
        }
        latest.Reverse();
        omitted = latest.Count > SessionWorkState.MaximumItems;
        if (latest.Count <= SessionWorkState.MaximumItems) return latest.ToArray();
        return [latest[0], .. latest.TakeLast(SessionWorkState.MaximumItems - 1)];
    }

    private static SessionWorkFact[] PreserveFirstIntent(SessionWorkFact[] facts)
    {
        if (facts.Length <= SessionWorkState.MaximumItems) return facts;
        return [facts[0], .. facts.TakeLast(SessionWorkState.MaximumItems - 1)];
    }

    private static SessionWorkFact[] OmitOldestNonAnchor(SessionWorkFact[] facts) =>
        facts.Length <= 1 ? facts : [facts[0], .. facts.Skip(2)];

    private static SessionWorkFact[] OmitOldestConstraint(SessionWorkFact[] facts) => facts.Length switch
    {
        0 => facts,
        1 => [],
        2 => [facts[^1]],
        _ => [facts[0], .. facts.Skip(2)],
    };

    private static bool IsAggregateCap(ArgumentException error) =>
        error.Message.Contains("serialized cap", StringComparison.Ordinal);
}
