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
        var skills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills", "reloadable")).FullName;
        File.WriteAllText(Path.Combine(skills, "SKILL.md"), "# Reloadable\n\nfirst skill body", new System.Text.UTF8Encoding(false));
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

        Assert.Equal(new[] { "root:Ru.md", "skill:reloadable" }, first.InstructionBlocks.Select(block => block.SourceId));
        File.WriteAllText(Path.Combine(skills, "SKILL.md"), "# Reloadable\n\nchanged skill body", new System.Text.UTF8Encoding(false));
        var third = await provider.BuildAsync(run, snapshot, CancellationToken.None);
        var firstSkill = first.InstructionBlocks.Single(block => block.SourceId == "skill:reloadable");
        var thirdSkill = third.InstructionBlocks.Single(block => block.SourceId == "skill:reloadable");
        Assert.NotEqual(firstSkill.ContentSha256, thirdSkill.ContentSha256);
        Assert.Contains("skill tool", thirdSkill.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed skill body", thirdSkill.Content, StringComparison.Ordinal);
        Assert.NotEqual(first.InstructionBlocks[0].ContentSha256, second.InstructionBlocks[0].ContentSha256);
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

        Assert.DoesNotContain(denied.Capabilities, capability => capability.Id == "web_search");
        Assert.DoesNotContain(denied.Capabilities, capability => capability.Id == "web_fetch");
        Assert.Contains(allowed.Capabilities, capability => capability.Id == "read" && capability.Availability == "enabled");
        Assert.Equal(12, allowed.Capabilities.Count);
        Assert.Equal(10, denied.Capabilities.Count);
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
        Assert.DoesNotContain(promptIds, id => !AgentToolDefinitionCatalog.Get().Any(definition => definition.ToolId == id));
        foreach (var capability in context.Capabilities)
        {
            var definition = AgentToolDefinitionCatalog.Find(capability.Id, capability.Version)!;
            Assert.Equal(definition.Scope, capability.Scope);
            Assert.Equal(AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema), capability.InputSchemaJson);
            Assert.Equal(AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), capability.InputSchemaDigest);
        }
    }

    [Fact]
    public async Task Keeps_the_newest_tool_results_instead_of_failing_over_the_reference_cap()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));

        // One reference per tool result used to be appended without a bound. Once a run had
        // accumulated more results than the runtime context accepts, the context constructor
        // rejected the whole prompt and the run ended with prompt_contract_invalid. A long
        // run therefore died in the middle of its work instead of losing the oldest output.
        const int budget = PromptRuntimeContext.MaxRuntimeReferences - 1;
        const int results = budget + 4;
        const int dropped = results - budget;
        var snapshot = ToolResultSnapshot(results);

        var context = await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);

        // The bound is the point: the constructed context proves it held.
        Assert.True(
            context.RuntimeContext.References.Count <= PromptRuntimeContext.MaxRuntimeReferences,
            $"references={context.RuntimeContext.References.Count} exceeds the Core cap");
        var omitted = Assert.Single(
            context.RuntimeContext.References,
            reference => reference.Kind == "tool_result_omitted");
        Assert.Contains($"{dropped} older tool result", omitted.Content, StringComparison.Ordinal);

        // The newest results survive, the oldest are the ones dropped. Read against the
        // constants rather than a hand-counted window, so the assertion cannot drift from
        // the budget it is meant to check.
        var kept = context.RuntimeContext.References
            .Where(reference => reference.Kind == "tool_result")
            .Select(reference => reference.Content)
            .ToArray();
        Assert.Equal(budget, kept.Length);
        Assert.Equal("tool output 999", kept[^1]);
        Assert.Equal($"tool output {dropped + 1}", kept[0]);
        Assert.DoesNotContain($"tool output {dropped} ", string.Join("\n", kept) + " ", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Carries_every_tool_result_reference_when_the_cap_is_not_reached()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));

        // Exactly at the tool-result budget: nothing is dropped, so the omission note that
        // would cost a slot is not emitted either.
        var snapshot = ToolResultSnapshot(PromptRuntimeContext.MaxRuntimeReferences - 1);

        var context = await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);

        Assert.Equal(PromptRuntimeContext.MaxRuntimeReferences - 1, context.RuntimeContext.References.Count);
        Assert.All(context.RuntimeContext.References, reference => Assert.Equal("tool_result", reference.Kind));
    }

    private static ModelContextSnapshot ToolResultSnapshot(int count)
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("user", 1, ModelContextRole.User, "read every note"),
        };
        for (var index = 1; index <= count; index++)
        {
            // The last result is numbered distinctly so the assertion can name which end of
            // the list survived without depending on the ordering implementation.
            var content = index == count ? "tool output 999" : $"tool output {index}";
            entries.Add(ModelContextEntry.ToolResult(
                $"tool-{index}",
                index + 1,
                ToolReceipt(index),
                content));
        }

        return new ModelContextSnapshot(entries, Array.Empty<byte>());
    }

    private static ToolExecutionReceipt ToolReceipt(int index) => new(
        new ExecutionId($"exec_{index:x32}"),
        new ToolCallId($"call_{index:x32}"),
        ToolExecutionOutcome.Succeeded,
        "ok",
        null,
        sideEffectsPossible: false);

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
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));

        var error = Assert.Throws<AgentProtocolException>(() => LoopbackModelStepRunner.BuildSettingsForTests(context));
        Assert.Equal("tool_contract_mismatch", error.Code);
    }

    private static SettingsSnapshot Settings() => new(
        new JsonObject(),
        "OpenAI",
        "test-model",
        CustomModel: "default",
        CustomUrl: "http://localhost:8000/v1",
        OpenAiModel: "test-model",
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
