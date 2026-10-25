using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ModelToolHeadroomRegressionTests
{
    [Fact]
    public void PowerShell_contract_uses_script_without_a_shell_selector()
    {
        var schema = AgentToolDefinitionCatalog.Get().Single(d => d.ToolId == "powershell").InputSchema;
        ToolSchemaValidator.Validate(schema, new JsonObject { ["script"] = "Write-Output hi" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(schema,
            new JsonObject { ["script"] = "Write-Output hi", ["shell"] = "cmd /c anything" }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Read_large_page_succeeds_and_exposes_a_valid_continuation(bool escapedUnicode, bool useDefault)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-read-headroom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var text = escapedUnicode ? new string('\u1EA5', 1_500_000) : new string('a', 300 * 1024);
            await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), text, new UTF8Encoding(false));
            var session = BrokerSession.CreateForHost(root, DateTimeOffset.UtcNow.AddMinutes(5));
            session.Open("headroom", new HashSet<string>([ToolScope.SystemRead, ToolScope.WorkspaceRead]));
            await using var service = new ToolExecutionService(session, root);
            var arguments = new JsonObject { ["path"] = "large.txt" };
            if (!useDefault) arguments["limit"] = 4 * 1024 * 1024;
            var result = await service.ExecuteAsync(new AgentEnvelope(1, "read-headroom", AgentMessageTypes.ToolRequest,
                "headroom", null, 30000, new JsonObject
                {
                    ["tool_id"] = "read", ["tool_version"] = "1.0.0",
                    ["arguments"] = arguments,
                }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            var visible = result.Output["content"]!.GetValue<string>();
            Assert.StartsWith(visible, text, StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetByteCount(visible), result.Output["bytes_read"]!.GetValue<int>());
            Assert.InRange(Encoding.UTF8.GetByteCount(result.Output.ToJsonString()), 1, 4 * 1024 * 1024);
            Assert.Equal(escapedUnicode, result.Output["truncated"]!.GetValue<bool>());
            await service.RemoveSessionAsync("headroom");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
