using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class ToolExecutionServiceTests
{
    [Fact]
    public async Task Edit_file_cannot_create_a_file_and_explains_the_write_call()
    {
        var fixture = CreateFullAccessService("ru-edit-create-");
        try
        {
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "edit-create", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "edit_file", ["tool_version"] = "1.0.0", ["capability_class"] = "internal",
                    ["run_id"] = fixture.RunId,
                    ["arguments"] = new JsonObject { ["path"] = "new.html", ["edits"] = new JsonArray(
                        new JsonObject { ["old_string"] = "existing text", ["new_string"] = "hello" }) },
                }), default);
            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Contains("write_file", result.SafeSummary);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace", "new.html")));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }
}
