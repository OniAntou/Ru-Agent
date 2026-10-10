using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Unit coverage for the shared mapping helpers the in-process gateway uses to
/// project runtime results into model-visible decisions and receipts. These
/// helpers are the single mapping implementation; the deleted wire gateway used
/// the same ones, so this suite preserves its helper coverage.
/// </summary>
public sealed class BrokerGatewayContractsTests
{
    [Fact]
    public void ApprovalArgsKey_canonicalizes_null_properties_without_throwing()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"path\":null,\"nested\":{\"z\":null,\"a\":1}}");

        var key = global::RuBackend.Harness.Broker.BrokerGatewayContracts.ApprovalArgsKey(
            "read",
            document.RootElement);

        Assert.Equal("read|{\"nested\":{\"a\":1,\"z\":null},\"path\":null}", key);
    }

    [Fact]
    public void BoundRepairHint_strips_lines_clamps_and_rejects_forbidden_fields()
    {
        Assert.Null(global::RuBackend.Harness.Broker.BrokerGatewayContracts.BoundRepairHint(null));
        Assert.Null(global::RuBackend.Harness.Broker.BrokerGatewayContracts.BoundRepairHint("   "));
        Assert.Null(global::RuBackend.Harness.Broker.BrokerGatewayContracts.BoundRepairHint("copy \"output\" from the file"));

        var bounded = global::RuBackend.Harness.Broker.BrokerGatewayContracts.BoundRepairHint(
            "line one\r\nline two " + new string('x', 600));

        Assert.NotNull(bounded);
        Assert.DoesNotContain('\r', bounded!);
        Assert.DoesNotContain('\n', bounded!);
        Assert.Equal(
            global::RuBackend.Harness.Broker.BrokerGatewayContracts.MaximumRepairHintLength,
            bounded!.Length);
        Assert.Equal(
            "old_string was not found in the file. Re-read the file and copy the exact text including whitespace.",
            global::RuBackend.Harness.Broker.BrokerGatewayContracts.BoundRepairHint(
                "  old_string was not found in the file. Re-read the file and copy the exact text including whitespace.  "));
    }

    [Fact]
    public void Failed_receipt_keeps_a_bounded_spool_id_and_drops_raw_output()
    {
        var execution = new PreparedToolExecution(
            new ExecutionId("exec_" + new string('5', 32)),
            new ToolCallId("call_" + new string('5', 32)),
            modelOrder: 1,
            ToolAccessKind.ReadOnly,
            preparationToken: "token",
            []);

        var receipt = global::RuBackend.Harness.Broker.BrokerGatewayContracts.FailedReceipt(
            execution,
            "process_exit_nonzero",
            safeSummary: null,
            output: new JsonObject
            {
                ["spool_id"] = "req-123",
                ["stderr_head"] = "raw diagnostic that must not cross",
            });

        var content = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span))!.AsObject();
        Assert.Equal("process_exit_nonzero", content["error"]!.GetValue<string>());
        Assert.Equal("req-123", content["spool_id"]!.GetValue<string>());
        Assert.False(content.ContainsKey("stderr_head"));
    }
}
