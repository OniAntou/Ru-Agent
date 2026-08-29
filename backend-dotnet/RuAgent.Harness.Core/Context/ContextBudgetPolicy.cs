namespace RuAgent.Harness.Core.Context;

public enum ContextCompactionStrategy
{
    Normal,
    Aggressive,
}

public sealed record ContextBudgetPolicyOptions(
    long OutputReserveTokens = 256,
    long SafetyMarginTokens = 64,
    double NormalTriggerRatio = 0.80,
    double NormalTargetRatio = 0.62,
    double AggressiveTargetRatio = 0.48)
{
    public ContextBudgetPolicyOptions Validate()
    {
        if (OutputReserveTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputReserveTokens));
        }

        if (SafetyMarginTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyMarginTokens));
        }

        ValidateRatio(NormalTriggerRatio, nameof(NormalTriggerRatio));
        ValidateRatio(NormalTargetRatio, nameof(NormalTargetRatio));
        ValidateRatio(AggressiveTargetRatio, nameof(AggressiveTargetRatio));
        return this;
    }

    private static void ValidateRatio(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed record ContextBudgetDecision(
    long EstimatedTokens,
    long MaxPromptTokens,
    long UsablePromptTokens,
    long SoftTriggerTokens,
    long TargetTokens,
    bool NeedsCompaction,
    ContextCompactionStrategy Strategy);

public sealed class ContextBudgetPolicy
{
    private readonly ContextBudgetPolicyOptions _options;

    public ContextBudgetPolicy(ContextBudgetPolicyOptions? options = null)
    {
        _options = (options ?? new ContextBudgetPolicyOptions()).Validate();
    }

    public ContextBudgetDecision Evaluate(
        long estimatedTokens,
        long maxPromptTokens,
        ContextCompactionStrategy strategy)
    {
        if (estimatedTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        }

        if (maxPromptTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPromptTokens));
        }

        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        var usable = Math.Max(
            1,
            maxPromptTokens - _options.OutputReserveTokens - _options.SafetyMarginTokens);
        var triggerRatio = _options.NormalTriggerRatio;
        var targetRatio = strategy == ContextCompactionStrategy.Normal
            ? _options.NormalTargetRatio
            : _options.AggressiveTargetRatio;
        var trigger = ClampToBudget((long)Math.Floor(usable * triggerRatio), usable);
        var target = ClampToBudget((long)Math.Floor(usable * targetRatio), usable);
        return new ContextBudgetDecision(
            estimatedTokens,
            maxPromptTokens,
            usable,
            trigger,
            target,
            estimatedTokens > trigger,
            strategy);
    }

    private static long ClampToBudget(long value, long usable) =>
        Math.Clamp(value, 1, usable);
}
