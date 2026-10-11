using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Settings;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.ModelRunner;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Bootstrap;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RuPromptContextProviderTests
{
    [Fact]
    public async Task Background_task_note_rides_along_as_a_bounded_reference()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)),
            webToolsEnabled: false)
        {
            BackgroundTaskNotes = _ => Task.FromResult<string?>("task req-1: running -> exited, exit 0"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        var note = context.RuntimeContext.References.Single(reference => reference.Kind == "background_tasks");
        Assert.Contains("req-1", note.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_task_note_probe_failure_stays_silent()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)),
            webToolsEnabled: false)
        {
            BackgroundTaskNotes = _ => throw new InvalidOperationException("probe failed"),
        };

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.DoesNotContain(context.RuntimeContext.References, reference => reference.Kind == "background_tasks");
    }

    [Fact]
    public async Task Prompt_model_label_comes_from_the_run_pin_without_reading_current_settings()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var resolver = new FixedMetadataResolver(new AgentRunModelMetadata(
            "model-a",
            "openai_responses",
            ProviderToolCallingMode.TextSelection,
            200_000,
            "models.dev",
            "catalog-v1",
            8_192));
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new ThrowingSettingsStore(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)),
            new AgentToolRuntimeAvailability(false, false),
            runModelMetadataResolver: resolver);

        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.Equal("model-a", context.Environment.Model);
        Assert.Equal(1, resolver.Calls);
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(context.SerializeUtf8()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builds_fresh_root_bound_context_and_keeps_tool_results_untrusted()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "root guidance", new System.Text.UTF8Encoding(false));
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
                         sideEffectsPossible: false,
                         providerCallId: "provider_call"),
                     "untrusted tool output"),
            },
            Array.Empty<byte>());
        var run = TestRun();

        var first = await provider.BuildAsync(run, snapshot, CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "changed guidance", new System.Text.UTF8Encoding(false));
        var second = await provider.BuildAsync(run, snapshot, CancellationToken.None);

        Assert.Equal(new[] { "root:AGENTS.md", "skill:reloadable" }, first.InstructionBlocks.Select(block => block.SourceId));
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

    [Theory]
    [InlineData(AgentPermissionPreset.Plan, "plan")]
    [InlineData(AgentPermissionPreset.Standard, "standard")]
    [InlineData(AgentPermissionPreset.FullAccess, "full_access")]
    public async Task Projects_the_canonical_effective_policy_for_the_run_preset(
        AgentPermissionPreset preset,
        string expectedWirePreset)
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));

        var context = await provider.BuildAsync(
            TestRun(preset),
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), Array.Empty<byte>()),
            CancellationToken.None);

        Assert.Equal(2, context.SchemaVersion);
        Assert.Equal(expectedWirePreset, context.Preset);
        Assert.Equal(expectedWirePreset, AgentPolicyProfileCatalog.Get(expectedWirePreset).Preset);
        Assert.Equal(
            AgentPolicyProfileCatalog.Get(expectedWirePreset).DecisionFor(AgentPolicySubject.ExternalTerminal),
            AgentPolicyProfileCatalog.Get(context.Preset).DecisionFor(AgentPolicySubject.ExternalTerminal));
        var serialized = System.Text.Encoding.UTF8.GetString(context.SerializeUtf8());
        Assert.Contains($"\"preset\":\"{expectedWirePreset}\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"effective_policy\":", serialized, StringComparison.Ordinal);
        var effectivePolicy = JsonNode.Parse(serialized)!["effective_policy"]!;
        Assert.Equal(
            AgentPolicyProfileCatalog.WriteProfile(AgentPolicyProfileCatalog.Get(expectedWirePreset)).ToJsonString(),
            effectivePolicy.ToJsonString());
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
        Assert.Equal(13, allowed.Capabilities.Count);
        Assert.Equal(11, denied.Capabilities.Count);
    }

    [Theory]
    [InlineData(false, false, 11)]
    [InlineData(true, false, 12)]
    [InlineData(false, true, 12)]
    [InlineData(true, true, 13)]
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
        var settings = InProcessModelStepRunner.BuildSettingsForTests(context);
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

    [Fact]
    public async Task Runtime_reference_cap_preserves_workspace_activity_and_newest_results()
    {
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        await harness.SeedRunAsync();
        var coordinator = new WorkspaceCoordinatorStore(harness.Options);
        var run = TestRun();
        await coordinator.GetOrCreateAsync(run.WorkspaceId, CancellationToken.None);
        await coordinator.AppendChangeAsync(
            run.WorkspaceId,
            null,
            "external-change-1",
            "external.txt",
            new string('a', 64),
            new string('b', 64),
            new EncryptedWorkspaceJournal(WorkspaceJournalProtector.CurrentProtectionVersion, new byte[12], new byte[] { 1 }, new byte[16]),
            "committed",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        using var instructions = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            instructions.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)),
            workspaceCoordinatorStore: coordinator);

        var context = await provider.BuildAsync(run, ToolResultSnapshot(PromptRuntimeContext.MaxRuntimeReferences), CancellationToken.None);

        Assert.Equal(PromptRuntimeContext.MaxRuntimeReferences, context.RuntimeContext.References.Count);
        Assert.Contains(context.RuntimeContext.References, reference => reference.Kind == "tool_result_omitted");
        Assert.Contains(context.RuntimeContext.References, reference => reference.Kind == "workspace_activity"
            && reference.Content.Contains("external.txt", StringComparison.Ordinal));
        Assert.Equal(PromptRuntimeContext.MaxRuntimeReferences - 2, context.RuntimeContext.References.Count(reference => reference.Kind == "tool_result"));
        Assert.Contains(context.RuntimeContext.References, reference => reference.Content.Contains("tool output 999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runtime_grep_reference_uses_exact_call_offset_and_keeps_source_receipt_unchanged()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));
        var results = new JsonArray();
        for (var index = 0; index < 4_000; index++)
            results.Add(new JsonObject { ["path"] = "notes.txt", ["line"] = index + 1, ["text"] = new string('x', 25) });
        var source = new JsonObject
        {
            ["results"] = results,
            ["count"] = results.Count,
            ["truncated"] = true,
            ["scanned_files"] = 1,
            ["scanned_directories"] = 1,
            ["skipped"] = 0,
            ["duration_ms"] = 1,
        }.ToJsonString();
        using var argsDocument = JsonDocument.Parse("{\"query\":\"needle\",\"path\":\".\",\"offset\":41,\"max_results\":4000}");
        var receipt = new ToolExecutionReceipt(
            new ExecutionId("exec_22222222222222222222222222222222"),
            new ToolCallId("call_22222222222222222222222222222222"),
            ToolExecutionOutcome.Succeeded,
            "ok",
            null,
            sideEffectsPossible: false,
            providerCallId: "provider_grep");
        var snapshot = new ModelContextSnapshot(
        [
            ModelContextEntry.Assistant("assistant", 1, new AssistantStep(null,
            [new ModelToolCall("provider_grep", "grep", "1.0.0", argsDocument.RootElement)],
                AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("result", 2, receipt, source),
        ], Array.Empty<byte>());

        var context = await provider.BuildAsync(TestRun(), snapshot, CancellationToken.None);

        var reference = Assert.Single(context.RuntimeContext.References);
        using var projected = JsonDocument.Parse(reference.Content);
        var root = projected.RootElement;
        Assert.InRange(Encoding.UTF8.GetByteCount(reference.Content), 1, PromptReference.ContentMaxBytes);
        Assert.Equal(4_000, root.GetProperty("count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.InRange(root.GetProperty("results").GetArrayLength(), 1, 4_000);
        Assert.Equal(41 + root.GetProperty("results").GetArrayLength(), root.GetProperty("projection_next_offset").GetInt32());
        Assert.Equal("call_22222222222222222222222222222222", root.GetProperty("projection_tool_call_id").GetString());
        Assert.Equal("provider_grep", root.GetProperty("projection_provider_call_id").GetString());
        Assert.Equal(source, snapshot.Entries[^1].Content);
    }

    [Fact]
    public async Task Oversized_grep_reference_without_exact_call_arguments_fails_with_a_named_code()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));
        var results = new JsonArray();
        for (var index = 0; index < 4_000; index++)
            results.Add(new JsonObject { ["path"] = "notes.txt", ["line"] = index + 1, ["text"] = new string('x', 25) });
        var source = new JsonObject
        {
            ["results"] = results,
            ["count"] = results.Count,
            ["truncated"] = true,
            ["scanned_files"] = 1,
            ["scanned_directories"] = 1,
            ["skipped"] = 0,
            ["duration_ms"] = 1,
        }.ToJsonString();
        var snapshot = new ModelContextSnapshot(
        [ModelContextEntry.ToolResult(
            "result",
            1,
            new ToolExecutionReceipt(
                new ExecutionId("exec_33333333333333333333333333333333"),
                new ToolCallId("call_33333333333333333333333333333333"),
                ToolExecutionOutcome.Succeeded,
                "ok",
                null,
                sideEffectsPossible: false,
                providerCallId: "provider_unknown"),
            source)],
            Array.Empty<byte>());

        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            provider.BuildAsync(TestRun(), snapshot, CancellationToken.None));

        Assert.Equal("tool_output_projection_invalid", error.Code);
    }

    [Fact]
    public async Task Unknown_underbudget_json_reference_preserves_legacy_content()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var provider = new RuPromptContextProvider(
            fixture.Root,
            new RuInstructionLoader(),
            new StubSettingsStore(Settings()),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));
        const string source = "{\"results\":[{\"value\":1}],\"count\":1,\"truncated\":false}";
        var context = await provider.BuildAsync(
            TestRun(),
            new ModelContextSnapshot(
            [ModelContextEntry.ToolResult("result", 1, ToolReceipt(1), source)], Array.Empty<byte>()),
            CancellationToken.None);

        Assert.Equal(source, Assert.Single(context.RuntimeContext.References).Content);
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
        sideEffectsPossible: false,
        providerCallId: $"provider_call_{index}");

    [Fact]
    public void Worker_settings_fail_closed_when_prompt_schema_digest_drifts()
    {
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}";
        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            "ru-agent",
            "standard",
            new PromptEnvironment("model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-02", "LLM"),
            Array.Empty<PromptInstructionBlock>(),
            [new PromptCapability("files.read", "1.0.0", "Read", "workspace.read", schema, new string('0', 64), null, "enabled")],
            new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));

        var error = Assert.Throws<AgentProtocolException>(() => InProcessModelStepRunner.BuildSettingsForTests(context));
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

    private static AgentRun TestRun(AgentPermissionPreset preset = AgentPermissionPreset.Standard) => new(
        new RunId("run_11111111111111111111111111111111"),
        new SessionId("11111111111111111111111111111111"),
        new WorkspaceId("ws_11111111111111111111111111111111"),
        new HarnessRunStatus(HarnessRunState.Running),
        1,
        0,
        100,
        0,
        0,
        preset,
        1,
        new string('a', 64),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Prompt metadata should not read current settings.");

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedMetadataResolver(AgentRunModelMetadata metadata) : IAgentRunModelMetadataResolver
    {
        public int Calls { get; private set; }

        public Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(metadata);
        }
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
