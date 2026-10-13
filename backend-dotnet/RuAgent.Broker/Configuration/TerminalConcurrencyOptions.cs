using RuAgent.Protocol;

namespace RuAgent.Broker.Configuration;

/// <summary>Broker-owned concurrent terminal process capacity.</summary>
public sealed record TerminalConcurrencyOptions(int MaxConcurrentTasks)
{
    public const string EnvironmentVariableName = "RU_MAX_BACKGROUND_TERMINAL_TASKS";
    public const int DefaultMaxConcurrentTasks = TerminalLimits.MaxBackgroundTasksPerSession;
    public const int MinimumMaxConcurrentTasks = 1;
    public const int MaximumMaxConcurrentTasks = 64;

    /// <summary>
    /// Reads the Broker setting at composition time. An invalid explicit value
    /// fails startup instead of silently disabling process admission control.
    /// </summary>
    public static TerminalConcurrencyOptions FromEnvironment(
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var rawValue = getEnvironmentVariable(EnvironmentVariableName);
        if (rawValue is null)
        {
            return new TerminalConcurrencyOptions(DefaultMaxConcurrentTasks);
        }

        if (!int.TryParse(rawValue, out var maxConcurrentTasks)
            || maxConcurrentTasks < MinimumMaxConcurrentTasks
            || maxConcurrentTasks > MaximumMaxConcurrentTasks)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} must be an integer from {MinimumMaxConcurrentTasks} to {MaximumMaxConcurrentTasks}.");
        }

        return new TerminalConcurrencyOptions(maxConcurrentTasks);
    }
}
