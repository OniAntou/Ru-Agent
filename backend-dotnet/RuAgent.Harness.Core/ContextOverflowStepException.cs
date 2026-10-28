namespace RuAgent.Harness.Core;

/// <summary>The provider rejected context; only the compaction owner can retry it.</summary>
public sealed class ContextOverflowStepException(Exception inner) : Exception("context_window_exceeded", inner), IStableStepError
{
    public string StepErrorCode => "context_window_exceeded";
}
