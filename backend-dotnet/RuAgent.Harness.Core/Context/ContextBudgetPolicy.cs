namespace RuAgent.Harness.Core.Context;

public enum ContextCompactionStrategy
{
    Normal,
    Aggressive,
}

public sealed record ContextBudgetPolicyOptions(
    long OutputReserveTokens = 256,
    long SafetyMarginTokens = 64,
    double NormalTriggerRatio = ContextBudgetPolicyOptions.DefaultNormalTriggerRatio,
    double NormalTargetRatio = ContextBudgetPolicyOptions.DefaultNormalTargetRatio,
    double AggressiveTargetRatio = 0.48)
{
    /// <summary>
    /// Single source for the compaction trigger default. Backend settings
    /// consts mirror this value (see SettingsSnapshot).
    /// </summary>
    public const double DefaultNormalTriggerRatio = 0.80;

    /// <summary>
    /// Single source for the compaction target default (see above).
    /// </summary>
    public const double DefaultNormalTargetRatio = 0.62;

    /// <summary>
    /// Finite, positive ratios within the policy range. Note this allows
    /// exactly 1.0: the compaction settings layer additionally requires
    /// trigger &lt; 1 (0 &lt; target &lt; trigger &lt; 1) and fails closed to
    /// defaults, so 1.0 never reaches a policy from settings.
    /// </summary>
    public static bool IsValidRatio(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= 1;

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
        if (!IsValidRatio(value))
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
