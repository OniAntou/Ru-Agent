using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class ToolExecutionServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("existing text")]
    public async Task Edit_cannot_create_a_file_and_explains_the_write_call(string oldText)
    {
        var fixture = CreateFullAccessService("ru-edit-create-");
        try
        {
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "edit-create", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "edit", ["tool_version"] = "1.0.0", ["capability_class"] = "internal",
                    ["run_id"] = fixture.RunId,
                    ["arguments"] = new JsonObject { ["path"] = "new.html", ["old_string"] = oldText, ["new_string"] = "hello" },
                }), default);
            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Contains("op", result.SafeSummary);
            Assert.Contains("write", result.SafeSummary);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace", "new.html")));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }
}
