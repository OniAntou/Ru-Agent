using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuBackend.Business.Settings;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.Worker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RuPromptContextProviderTests
{
    [Fact]
    public async Task Builds_fresh_root_bound_context_and_keeps_tool_results_untrusted()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Ru.md"), "root guidance", new System.Text.UTF8Encoding(false));
        var settings = new StubSettingsStore(Settings());
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            settings,
            time,
            webToolsEnabled: false);
        var snapshot = new ModelContextSnapshot(
            new ModelContextEntry[]
            {
                ModelContextEntry.Text("base:memory", 1, ModelContextRole.System, "remembered preference"),
                ModelContextEntry.Text("user", 2, ModelContextRole.User, "read README"),
                ModelContextEntry.ToolResult(
                    "tool",
                    3,
                    new ToolExecutionReceipt(
                        new ExecutionId("exec_11111111111111111111111111111111"),
                        new ToolCallId("call_11111111111111111111111111111111"),
                        ToolExecutionOutcome.Succeeded,
                        "ok",
                        null,
                        sideEffectsPossible: false),
                    "untrusted tool output"),
            },
            Array.Empty<byte>());
        var run = TestRun();

        var first = await provider.BuildAsync(run, snapshot, CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture.Root, "Ru.md"), "changed guidance", new System.Text.UTF8Encoding(false));
        var second = await provider.BuildAsync(run, snapshot, CancellationToken.None);

        Assert.Equal("root:Ru.md", Assert.Single(first.InstructionBlocks).SourceId);
        Assert.NotEqual(first.InstructionBlocks[0].ContentSha256, second.InstructionBlocks[0].ContentSha256);
        Assert.Equal("remembered preference", first.RuntimeContext.Memory);
        Assert.Single(first.RuntimeContext.References);
        Assert.Equal("tool_result", first.RuntimeContext.References[0].Kind);
        Assert.DoesNotContain("arguments", first.RuntimeContext.References[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base:memory", first.RuntimeContext.References[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exposes_web_tools_only_when_runtime_wiring_is_enabled()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var settings = new StubSettingsStore(Settings());
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var context = new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>());
        var run = TestRun();

        var denied = await new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            settings,
            time,
            webToolsEnabled: false).BuildAsync(run, context, CancellationToken.None);
        var allowed = await new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            settings,
            time,
            webToolsEnabled: true).BuildAsync(run, context, CancellationToken.None);

        Assert.DoesNotContain(denied.Capabilities, capability => capability.Id == "web.search");
        Assert.DoesNotContain(denied.Capabilities, capability => capability.Id == "web.fetch");
        Assert.Contains(allowed.Capabilities, capability =>
            capability.Id == "web.search"
            && capability.Availability == "enabled"
            && capability.Scope == "network.search");
        Assert.Contains(allowed.Capabilities, capability =>
            capability.Id == "web.fetch"
            && capability.Availability == "enabled"
            && capability.Scope == "network.fetch");
    }

    [Theory]
    [InlineData(false, false, 10)]
    [InlineData(true, false, 11)]
    [InlineData(false, true, 11)]
    [InlineData(true, true, 12)]
    public async Task Prompt_and_worker_share_the_same_runtime_projection(bool search, bool fetch, int expectedCount)
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)),
            new AgentToolRuntimeAvailability(search, fetch));
        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);
        var settings = LoopbackModelStepRunner.BuildSettingsForTests(context);
        var promptIds = context.Capabilities.Select(capability => capability.Id).ToArray();
        var workerIds = settings["model_tools"]!.AsArray()
            .Select(node => node!["tool_id"]!.GetValue<string>())
            .ToArray();

        Assert.Equal(expectedCount, promptIds.Length);
        Assert.Equal(promptIds, workerIds);
        Assert.DoesNotContain(promptIds, id => !AgentToolDefinitionCatalog.Select(AgentToolExposure.ModelVisible).Any(definition => definition.ToolId == id));
        foreach (var capability in context.Capabilities)
        {
            var definition = AgentToolDefinitionCatalog.Find(capability.Id, capability.Version)!;
            Assert.Equal(definition.Scope, capability.Scope);
            Assert.Equal(definition.InputSchema.ToJsonString(), capability.InputSchemaJson);
            Assert.Equal(AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), capability.InputSchemaDigest);
        }
    }

    [Fact]
    public void Worker_settings_fail_closed_when_prompt_schema_digest_drifts()
    {
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}";
        var context = new SystemPromptContext(
            1,
            "ru-agent",
            "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-02", "LLM"),
            Array.Empty<PromptInstructionBlock>(),
            [new PromptCapability("files.read", "1.0.0", "Read", "workspace.read", schema, new string('0', 64), null, "enabled")],
            new PromptRuntimeContext(null, null, null, null, null, Array.Empty<PromptReference>()));

        var error = Assert.Throws<AgentProtocolException>(() => LoopbackModelStepRunner.BuildSettingsForTests(context));
        Assert.Equal("tool_contract_mismatch", error.Code);
    }

    private static SettingsSnapshot Settings() => new(
        new JsonObject(),
        "OpenCode Zen",
        "test-model",
        "test-model",
        "test-model",
        "Default",
        EnableSelfLearning: false,
        ShareMemoryWithCloud: false,
        KiloReasoningEnabled: false,
        VoiceLabel: "Ru",
        VoiceStyleId: "original",
        VoiceSpeed: 1,
        AutoPaceEnabled: false,
        AutoReadReplies: true,
        ShowSubtitles: false,
        SubtitleColor: "#FFFFFF",
        SubtitleSize: "medium",
        SubtitleDuration: 2,
        SubtitlePosMode: "auto",
        SubtitlePosX: -1,
        SubtitlePosY: -1,
        OllamaModel: "qwen",
        LmStudioModel: "default",
        CustomModel: "default",
        OllamaUrl: "http://localhost:11434/v1",
        LmStudioUrl: "http://localhost:1234/v1",
        CustomUrl: "http://localhost:8000/v1",
        OpenAiModel: string.Empty,
        AnthropicModel: string.Empty);

    private static AgentRun TestRun() => new(
        new RunId("run_11111111111111111111111111111111"),
        new SessionId("11111111111111111111111111111111"),
        new WorkspaceId("ws_11111111111111111111111111111111"),
        new HarnessRunStatus(HarnessRunState.Running),
        1,
        0,
        100,
        0,
        0,
        AgentPermissionPreset.Standard,
        1,
        new string('a', 64),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RuInstructionLoaderTestsRoot : IDisposable
    {
        private RuInstructionLoaderTestsRoot(string root) => Root = root;
        public string Root { get; }
        public static RuInstructionLoaderTestsRoot Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-prompt-provider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new RuInstructionLoaderTestsRoot(root);
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
