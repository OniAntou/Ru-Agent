using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

internal static class SubagentResultRow
{
    internal static void Validate(long version, long attempt, HarnessRunState state, HarnessRunState executionState)
    {
        if (version != attempt || version < 1 || version > SubagentLimits.MaxAttempts)
            throw new HarnessStoreException("subagent_result_version_invalid");
        if (!state.IsTerminal() || !executionState.IsTerminal()) throw new HarnessStoreException("subagent_result_not_terminal");
    }
}
