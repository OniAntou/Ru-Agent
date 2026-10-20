using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;

namespace RuBackend.Tests.Harness;

/// <summary>A clock frozen at one instant, for tests whose attempts were admitted at a fixture timestamp.</summary>
internal sealed class FixedTestClock(DateTimeOffset now) : ITimeProvider
{
    public DateTimeOffset UtcNow { get; } = now;
}

internal sealed record SubagentCoordinatorFactoryTestDependencies(
    SubagentRuntimeBinder Binder,
    IAgentFamilyBudget FamilyBudget,
    ISubagentInbox Inbox)
{
    public static SubagentCoordinatorFactoryTestDependencies Create(
        BackendHarnessTestFixture fixture,
        IToolGatewayFactory gateways,
        SqliteSubagentStore? store = null,
        ITimeProvider? budgetTime = null)
    {
        store ??= new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        return new(
            new SubagentRuntimeBinder(fixture.EventStore, store,
                new SqliteSubagentScopeStore(fixture.Options, new WorkspaceJournalProtector(fixture.KeyStore)), gateways),
            new SubagentFamilyBudget(fixture.EventStore, store,
                new SqliteSubagentBudgetStore(fixture.Options), budgetTime ?? new SystemTimeProvider()),
            store);
    }
}
