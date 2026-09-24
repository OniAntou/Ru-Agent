namespace RuAgent.Harness.Core;

/// <summary>
/// Marks an exception that already carries its own bounded terminal code, so the
/// model-step classifier keeps it instead of collapsing everything to
/// <c>model_failed</c>. The code must satisfy the terminal shape or it is
/// ignored. Implemented outside Core (which stays dependency-free); consumed
/// only by the classifier.
/// </summary>
public interface IStableStepError
{
    string StepErrorCode { get; }
}
