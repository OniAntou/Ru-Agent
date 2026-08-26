namespace RuAgent.Harness.Core.Reconciliation;

/// <summary>
/// The typed user decision for one ambiguous tool outcome awaiting reconciliation.
/// </summary>
public enum ReconciliationCommandKind
{
    Applied,
    NotApplied,
    UnknownContinue,
}

public readonly record struct ReconciliationCommand(ReconciliationCommandKind Kind)
{
    public static ReconciliationCommand Applied => new(ReconciliationCommandKind.Applied);

    public static ReconciliationCommand NotApplied => new(ReconciliationCommandKind.NotApplied);

    public static ReconciliationCommand UnknownContinue => new(ReconciliationCommandKind.UnknownContinue);

    /// <summary>Wire tokens are lowercase snake_case; anything else fails closed.</summary>
    public static bool TryParse(string? token, out ReconciliationCommand command)
    {
        command = default;
        switch (token)
        {
            case "applied":
                command = Applied;
                return true;
            case "not_applied":
                command = NotApplied;
                return true;
            case "unknown_continue":
                command = UnknownContinue;
                return true;
            default:
                return false;
        }
    }

    public string ToWireToken() => Kind switch
    {
        ReconciliationCommandKind.Applied => "applied",
        ReconciliationCommandKind.NotApplied => "not_applied",
        ReconciliationCommandKind.UnknownContinue => "unknown_continue",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };
}
