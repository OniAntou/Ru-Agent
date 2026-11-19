using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// ADR 0053 through the dispatcher: a failed read keeps its code and its exception type, and
/// the hint the adapter attached becomes the failure summary the model reads.
/// </summary>
public sealed partial class ToolExecutionServiceTests
{
    [Fact]
    public async Task A_non_utf8_read_names_the_decoding_command_instead_of_rejected_arguments()
    {
        var fixture = CreateLeaseService("ru-read-hint-utf8-", EnvelopeTestSnapshots.StandardEmpty());
        try
        {
            File.WriteAllBytes(Path.Combine(fixture.Root, "workspace", "legacy.txt"), [0x43, 0x61, 0x66, 0xE9]);

            var result = await ReadTool(fixture, "read", "legacy.txt");

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("invalid_utf8", result.ErrorCode);
            Assert.Contains("Get-Content -Encoding Default", result.SafeSummary, StringComparison.Ordinal);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    public async Task A_read_outside_the_workspace_says_how_to_attach_the_file()
    {
        var fixture = CreateLeaseService("ru-read-hint-outside-", EnvelopeTestSnapshots.StandardEmpty());
        try
        {
            var outside = Path.Combine(fixture.Root, "elsewhere.txt");
            File.WriteAllText(outside, "x");

            var result = await ReadTool(fixture, "read", outside);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("outside_workspace", result.ErrorCode);
            Assert.Contains(".ru/attachments", result.SafeSummary, StringComparison.Ordinal);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    public async Task A_line_read_of_a_pdf_points_at_read()
    {
        var fixture = CreateLeaseService("ru-read-hint-lines-", EnvelopeTestSnapshots.StandardEmpty());
        try
        {
            File.WriteAllBytes(Path.Combine(fixture.Root, "workspace", "spec.pdf"), "%PDF-1.4"u8.ToArray());

            var result = await ReadTool(fixture, "read_file", "spec.pdf");

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("read_file_text_only", result.ErrorCode);
            Assert.Contains("Call read", result.SafeSummary, StringComparison.Ordinal);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

    private static Task<ToolResult> ReadTool(
        (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) fixture,
        string toolId,
        string path) =>
        fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2, "read-hint-" + Guid.NewGuid().ToString("N"), AgentMessageTypes.ToolExecute,
            fixture.SessionId, null, 30_000, new JsonObject
            {
                ["tool_id"] = toolId, ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                ["capability_class"] = "internal",
                ["arguments"] = new JsonObject { ["path"] = path },
            }), default);
}
