namespace RuAgent.Client;

/// <summary>Why a run is parked, in one sentence plus the next useful step.</summary>
public sealed record ParkReasonCopy(string Headline, string Advice);

/// <summary>
/// The shared wording for a parked run, keyed by the bounded safe code the Host
/// commits on <c>continue.requested</c>. A code outside the table keeps its raw
/// token, so an unknown park reason stays visible instead of being hidden.
/// </summary>
/// <remarks>
/// The codes are machine tokens (<c>provider_transport_error</c> names a
/// transport failure, not a sentence). The terminal, the desktop activity row
/// and the status line all resolve their park copy here so they cannot tell the
/// user different stories about the same pause. The Host owns the decision and
/// the code; this table is presentation only.
/// </remarks>
public static class AgentParkReasonCopy
{
    /// <summary>Every catalogued park code, keyed by its exact wire token.</summary>
    public static IReadOnlyDictionary<string, ParkReasonCopy> Table { get; } = BuildTable();

    /// <summary>Resolves a park code to its catalogued copy; blank and unknown codes return false.</summary>
    public static bool TryGet(string? code, out ParkReasonCopy? copy)
    {
        var key = (code ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            copy = null;
            return false;
        }

        return Table.TryGetValue(key, out copy);
    }

    /// <summary>
    /// The one-line pause sentence for a status surface, or empty for an unknown
    /// code so the caller can keep showing the raw token.
    /// </summary>
    public static string Compose(string? code)
    {
        if (!TryGet(code, out var copy) || copy is null)
        {
            return string.Empty;
        }

        return $"Paused: {Capitalize(copy.Headline)}. {Capitalize(copy.Advice)}. Send Continue when ready.";
    }

    /// <summary>The short "why" sentence without the action, for an activity row.</summary>
    public static string Headline(string? code) =>
        TryGet(code, out var copy) && copy is not null
            ? "Paused: " + Capitalize(copy.Headline)
            : string.Empty;

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static Dictionary<string, ParkReasonCopy> BuildTable()
    {
        var table = new Dictionary<string, ParkReasonCopy>(StringComparer.Ordinal);

        // Provider reachability: the model step threw before a reply existed, so
        // these are the only parks where the user can wait and try again.
        table["provider_transport_error"] = new(
            "network connection to the provider failed",
            "check your network first");
        table["provider_timeout"] = new(
            "the provider stopped responding",
            "wait a moment first");
        table["provider_server_error"] = new(
            "the provider returned server errors",
            "wait a moment first");
        table["provider_rate_limited"] = new(
            "the provider is rate-limiting requests",
            "wait a moment first");
        table["provider_quota_exhausted"] = new(
            "the provider account is out of quota",
            "restore provider credits or limits first");

        // Budgets and context: the run is intact, but the window that lets it
        // continue is spent.
        table["model_step_budget_exhausted"] = new(
            "the run used its model step budget",
            "a fresh budget window is available");
        table["tool_call_budget_exhausted"] = new(
            "the run used its tool call budget",
            "a fresh budget window is available");
        table["subagent_family_budget_exhausted"] = new(
            "the run used its sub-agent budget",
            "a fresh budget window is available");
        table["context_limit_reached"] = new(
            "the context window is full",
            "compact the session first");

        // Model behaviour and an ambiguous mutation. The Host reports the pause
        // event with recovery_ambiguous; the run state and the API surface use
        // reconciliation_required. Both name the same decision.
        table["repeated_tool_call"] = new(
            "the model repeated the same action",
            "change the request first");
        var reconciliation = new ParkReasonCopy(
            "the last action's result is unverified",
            "a read-only inspection comes first");
        table["reconciliation_required"] = reconciliation;
        table["recovery_ambiguous"] = reconciliation;

        return table;
    }
}
