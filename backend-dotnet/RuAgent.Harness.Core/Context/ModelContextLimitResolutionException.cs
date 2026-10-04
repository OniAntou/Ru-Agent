namespace RuAgent.Harness.Core.Context;

public sealed class ModelContextLimitResolutionException : Exception, IStableStepError
{
    public ModelContextLimitResolutionException(string stepErrorCode, Exception? innerException = null)
        : base("The run's pinned model context metadata could not be resolved.", innerException)
    {
        if (!IsStableCode(stepErrorCode))
        {
            throw new ArgumentException("The context metadata error code is invalid.", nameof(stepErrorCode));
        }

        StepErrorCode = stepErrorCode;
    }

    public string StepErrorCode { get; }

    private static bool IsStableCode(string? value) => value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_');
}
