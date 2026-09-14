using System.Diagnostics;

namespace RuAgent.Harness.Core.Diagnostics;

/// <summary>
/// OpenTelemetry seam for the harness model-step phase. Runs parallel to the
/// JSONL activity stream and never replaces it: when no listener is attached
/// <see cref="StartModelStepActivity"/> returns null and every helper is a no-op.
/// Privacy: only bounded outcome codes are recorded. Run ids, prompts, tool
/// arguments, paths and payloads must never become tags.
/// </summary>
public static class HarnessTelemetry
{
    public const string SourceName = "RuAgent.Harness";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts an <c>agent.model_step</c> span, or null without a listener.</summary>
    public static Activity? StartModelStepActivity() =>
        Source.StartActivity("agent.model_step", ActivityKind.Internal);

    /// <summary>
    /// Records the step outcome. Unknown codes are dropped fail-closed so a
    /// caller typo can never leak an unexpected value into exported traces.
    /// </summary>
    public static void SetStepOutcome(Activity? activity, string? outcomeCode)
    {
        if (activity is null)
        {
            return;
        }

        if (outcomeCode is "succeeded" or "cancelled" or "model_failed" or "prompt_contract_invalid")
        {
            activity.SetTag("step.outcome", outcomeCode);
            activity.SetStatus(outcomeCode == "succeeded" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }
    }
}
