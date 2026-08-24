namespace RuAgent.Harness.Core;

public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
