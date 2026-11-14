using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

/// <summary>What the main model had in front of it when it asked for advice (ADR 0047).</summary>
public sealed record AdvisorConsultationSource(
    ModelContextSnapshot Context,
    SystemPromptContext PromptContext,
    string? AssistantText);

/// <summary>One consultation: the run's context plus an optional user-named model and effort.</summary>
public sealed record AdvisorConsultationRequest(
    RunId RunId,
    ModelStepId ModelStepId,
    AdvisorConsultationSource Source,
    string? RequestedModel,
    string? RequestedEffort);

/// <summary>The advice, or the named reason there is none. Advice is untrusted model output.</summary>
public sealed record AdvisorConsultationResult(
    string? Advice,
    string? ErrorCode,
    string? AdvisorModel = null,
    bool HistoryShortened = false,
    ModelUsage? Usage = null)
{
    public static AdvisorConsultationResult Failed(string code) => new(null, code);
}

/// <summary>
/// Makes one tool-less request to the run's advisor. Implementations never dispatch tools,
/// never substitute another model, and report failures as named codes rather than exceptions;
/// only cancellation propagates.
/// </summary>
public interface IAdvisorConsultant
{
    Task<AdvisorConsultationResult> ConsultAsync(AdvisorConsultationRequest request, CancellationToken cancellationToken);
}

/// <summary>The advisor's named failure codes (ADR 0047, decisions 2a and 9).</summary>
public static class AdvisorCodes
{
    public const string ToolId = "advisor";
    public const string Prefix = "advisor_";
    public const string NotSelected = "advisor_not_selected";
    public const string Unavailable = "advisor_unavailable";
    public const string SubagentDenied = "advisor_subagent_denied";
    public const string MustBeAlone = "advisor_must_be_alone";
    public const string Interrupted = "advisor_interrupted";
    public const string ArgumentsInvalid = "advisor_arguments_invalid";
    public const string ProviderFailed = "advisor_provider_failed";
    public const string Refused = "advisor_refused";
    public const string Incomplete = "advisor_incomplete";
    public const string ContextTooLarge = "advisor_context_too_large";
    public const string ModelUserRequestRequired = "advisor_model_user_request_required";
    public const string EffortUserRequestRequired = "advisor_effort_user_request_required";
    public const string ModelUnavailable = "advisor_model_unavailable";
    public const string ModelAmbiguous = "advisor_model_ambiguous";
    public const string CatalogUnavailable = "advisor_catalog_unavailable";
    public const string EffortUnsupported = "advisor_effort_unsupported";

    /// <summary>An advisor failure informs the model; it is never a reason to fail the run.</summary>
    public static bool IsAdvisorFailure(string? code) =>
        code is not null && code.StartsWith(Prefix, StringComparison.Ordinal);
}
