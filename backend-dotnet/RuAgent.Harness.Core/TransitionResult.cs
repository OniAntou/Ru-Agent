namespace RuAgent.Harness.Core;

public readonly record struct TransitionResult<T>(
    bool Accepted,
    T Next,
    string? ErrorCode)
{
    public static TransitionResult<T> Accept(T next) => new(true, next, null);

    public static TransitionResult<T> Reject(T current, string errorCode) =>
        new(false, current, errorCode);
}
