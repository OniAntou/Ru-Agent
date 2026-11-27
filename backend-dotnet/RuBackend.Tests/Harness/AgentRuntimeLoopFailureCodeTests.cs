using RuBackend.Harness;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The terminal code a run ends with is the only handle a user or a support
/// report has. A failure that already names its family must keep that name
/// instead of collapsing into the driver catch-all.
/// </summary>
public sealed class AgentRuntimeLoopFailureCodeTests
{
    [Fact]
    public void A_store_failure_keeps_its_own_code()
    {
        Assert.Equal(
            "harness_store_unavailable",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(new HarnessStoreException("harness_store_unavailable")));
    }

    [Fact]
    public void A_wrapper_holding_one_failure_keeps_that_failures_code()
    {
        Assert.Equal(
            "harness_store_unavailable",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AggregateException(new HarnessStoreException("harness_store_unavailable"))));
        Assert.Equal(
            "runtime_drive_failed",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AggregateException(new HarnessStoreException("a_code"), new HarnessStoreException("b_code"))));
    }

    [Fact]
    public void An_untyped_database_failure_names_the_store()
    {
        Assert.Equal(
            "agent_store_unavailable",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new Microsoft.Data.Sqlite.SqliteException("database is locked", 5)));
    }

    [Fact]
    public void A_protocol_failure_keeps_its_own_code()
    {
        Assert.Equal(
            "unsupported_protocol_version",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AgentProtocolException("unsupported_protocol_version")));
    }

    [Fact]
    public void A_transport_failure_keeps_the_code_it_carries()
    {
        Assert.Equal(
            "broker_gateway_unavailable",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new ToolTransportException("broker_gateway_unavailable")));
    }

    [Fact]
    public void A_transport_message_that_is_prose_is_not_mistaken_for_a_code()
    {
        Assert.Equal(
            "runtime_drive_failed",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new ToolTransportException("The Broker connection failed.")));
    }

    [Fact]
    public void A_lost_run_state_race_names_the_conflict_instead_of_the_fallback()
    {
        Assert.Equal(
            "run_commit_conflict",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AgentRunStateException("run_version_conflict")));
        // A contract guard means a bug, not a known family: the fallback still names it.
        Assert.Equal(
            "runtime_drive_failed",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AgentRunStateException("unknown_commit_outcome")));
    }

    [Fact]
    public void A_bounded_condition_thrown_by_a_step_keeps_its_code()
    {
        Assert.Equal(
            "provider_continuation_too_large",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new AgentProtocolException("provider_continuation_too_large")));
        Assert.Equal(
            "broker_gateway_requires_canonical_snapshot_hash",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(
                new ToolTransportException("broker_gateway_requires_canonical_snapshot_hash")));
    }

    [Fact]
    public void An_unclassified_failure_still_falls_back_to_the_driver_code()
    {
        // The catch-all stays for a genuinely unknown fault, so the family mapping
        // above never invents a reason.
        Assert.Equal(
            "runtime_drive_failed",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(new InvalidOperationException("unknown")));
    }
}
