using System.Globalization;

namespace RuAgent.Protocol.Subagents;

/// <summary>
/// How many admitted child attempts one root family may run at once. The Host
/// reads this once at startup; invalid or out-of-range values fail closed to the
/// protocol default rather than granting more concurrency by typo.
/// </summary>
public sealed class SubagentConcurrencyOptions
{
    public const string EnvironmentVariableName = "RU_MAX_RUNNABLE_SUBAGENTS";
    public const int Minimum = 1;
    public const int Maximum = SubagentLimits.MaxIdentities;

    public int MaxRunnable { get; init; } = SubagentLimits.MaxRunnable;

    public static SubagentConcurrencyOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var raw = environment(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= Minimum and <= Maximum)
        {
            return new SubagentConcurrencyOptions { MaxRunnable = value };
        }

        return new SubagentConcurrencyOptions();
    }
}
