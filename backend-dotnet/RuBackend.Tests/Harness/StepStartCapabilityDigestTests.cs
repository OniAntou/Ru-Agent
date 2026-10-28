using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuBackend.Business.Settings;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.ModelRunner;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

/// <summary>
/// The composer writes capability digests with the key-sorted hash while the
/// capability JSON keeps declaration order, so the model-tool projection must
/// compare against the same sorted hash. A document-order comparison rejects
/// every real tool.
/// </summary>
public sealed class StepStartCapabilityDigestTests
{
    [Fact]
    public async Task Production_like_context_passes_the_model_tool_projection()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-step-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new StubSettingsStore(new SettingsSnapshot(
                new JsonObject(), "OpenAI", "gpt-5.6-terra",
                CustomModel: string.Empty, CustomUrl: "http://localhost:8000/v1",
                OpenAiModel: "gpt-5.6-terra", AnthropicModel: string.Empty));
            var provider = new RuPromptContextProvider(
                root, new RuInstructionLoader(), settings,
                new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero)),
                webToolsEnabled: true);
            var snapshot = new ModelContextSnapshot(
                new ModelContextEntry[] { ModelContextEntry.Text("user", 2, ModelContextRole.User, ".") },
                Array.Empty<byte>());
            var run = new AgentRun(
                new RunId("run_" + new string('3', 32)),
                new SessionId(new string('1', 32)),
                new WorkspaceId("ws_" + new string('2', 32)),
                new HarnessRunStatus(HarnessRunState.Running),
                0, 0, 1, 0, 0, AgentPermissionPreset.Standard, 1, new string('a', 64),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            var context = await provider.BuildAsync(run, snapshot, CancellationToken.None);
            Assert.NotEmpty(context.Capabilities);
            var projected = InProcessModelStepRunner.BuildSettingsForTests(context);
            Assert.Equal(context.Capabilities.Count, projected["model_tools"]!.AsArray().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Every_catalog_definition_survives_the_model_tool_projection()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        var capabilities = AgentToolDefinitionCatalog.Get().Select(definition => new PromptCapability(
            definition.ToolId,
            definition.ToolVersion,
            definition.Description,
            definition.Scope,
            // The one JSON form: CanonicalSchemaJson is what every capability uses,
            // so the string and its digest describe the same bytes.
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
            definition.ExampleArguments?.ToJsonString(options),
            "enabled")).ToArray();
        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion, "ru-agent", "standard",
            new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-08-29", "LLM"),
            Array.Empty<PromptInstructionBlock>(),
            capabilities,
            new PromptRuntimeContext(Array.Empty<PromptReference>()));
        var projected = InProcessModelStepRunner.BuildSettingsForTests(context);
        Assert.Equal(capabilities.Length, projected["model_tools"]!.AsArray().Count);
    }

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
