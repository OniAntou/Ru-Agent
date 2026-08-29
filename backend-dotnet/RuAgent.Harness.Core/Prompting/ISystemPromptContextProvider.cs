using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

public interface ISystemPromptContextProvider
{
    Task<SystemPromptContext> BuildAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken);
}
