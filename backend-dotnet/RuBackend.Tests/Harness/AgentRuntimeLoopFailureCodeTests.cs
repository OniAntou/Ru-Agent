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
    public void An_unclassified_failure_still_falls_back_to_the_driver_code()
    {
        // The catch-all stays for a genuinely unknown fault, so the family mapping
        // above never invents a reason.
        Assert.Equal(
            "runtime_drive_failed",
            AgentRuntimeLoopService.NormalizeDriveFailureCode(new InvalidOperationException("unknown")));
    }
}
