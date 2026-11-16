namespace RuAgent.Protocol.Harness;

/// <summary>
/// The context window a run assumes when the selected provider catalog publishes no
/// limit for its model. Current chat models serve around a million tokens, and the
/// source is recorded as <c>assumed</c> on the run pin so the choice stays visible.
/// A provider that serves less still rejects an oversized request with its own
/// bounded context error, which the run recovers from by compacting under the same
/// pinned selection; a provider that serves more never wastes the published headroom.
/// A named custom provider can declare its exact window per model instead.
/// </summary>
public static class ContextWindowDefaults
{
    /// <summary>The window assumed when neither the catalog nor a custom provider declares one.</summary>
    public const long UnknownContextWindowTokens = 1_048_576;
}
