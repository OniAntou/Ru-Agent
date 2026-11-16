using System.Globalization;

namespace RuAgent.Protocol.Subagents;

/// <summary>
/// How many admitted child attempts one root family may run at once. There is no
/// limit by default. A user who wants one (for example to stay under a provider's
/// rate limit) sets a positive integer; the Host reads it once at startup, and an
/// invalid value keeps the default rather than imposing a cap the user did not mean.
/// </summary>
public sealed class SubagentConcurrencyOptions
{
    public const string EnvironmentVariableName = "RU_MAX_RUNNABLE_SUBAGENTS";
    public const int Minimum = 1;

    /// <summary>The admitted-child cap, or null for no limit.</summary>
    public int? MaxRunnable { get; init; }

    public static SubagentConcurrencyOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var raw = environment(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= Minimum)
        {
            return new SubagentConcurrencyOptions { MaxRunnable = value };
        }

        return new SubagentConcurrencyOptions();
    }
}
