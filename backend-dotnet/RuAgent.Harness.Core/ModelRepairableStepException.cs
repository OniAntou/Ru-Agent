namespace RuAgent.Harness.Core;

/// <summary>
/// A model step the runner rejected with a stable code the model can repair by
/// taking another turn (malformed, unavailable or invalid tool call/selection).
/// Carries the runner's code plus the original exception; the coordinator either
/// converts it into a bounded recovery note or, once the notes are spent, marks
/// the step failure and lets this escape so terminal behaviour matches the
/// pre-recovery path.
/// </summary>
public sealed class ModelRepairableStepException : Exception
{
    public ModelRepairableStepException(string code, Exception innerException)
        : base(code, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(innerException);
        Code = code;
    }

    /// <summary>The runner's stable step-error code, safe for the transcript.</summary>
    public string Code { get; }
}
