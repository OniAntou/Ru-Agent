using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class RuPromptContextProviderTests
{
    [Fact]
    public void Nearest_instruction_survives_empty_ancestors_and_directory_limit()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var relative = Path.Combine(Enumerable.Range(1, 9).Select(i => "d" + i).ToArray());
        var directory = Path.Combine(fixture.Root, relative);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "NEAREST_REQUIRED_RULE");
        var loader = new RuInstructionLoader(localApplicationDataRoot: fixture.Root);
        var result = loader.Load(fixture.Root, [Path.Combine(relative, "file.cs")]);
        Assert.Contains(result.Blocks, block => block.Content.Contains("NEAREST_REQUIRED_RULE"));
        Assert.DoesNotContain(result.Skips, skip => skip.Reason == "directory_limit");

        // Even when every ancestor has guidance, the file's nearest scope must win a bounded slot.
        var current = fixture.Root;
        for (var i = 1; i <= 8; i++)
        {
            current = Path.Combine(current, "d" + i);
            File.WriteAllText(Path.Combine(current, "AGENTS.md"), "ancestor " + i);
        }
        result = loader.Load(fixture.Root, [Path.Combine(relative, "file.cs")]);
        var nested = result.Blocks.Where(block => block.SourceId.StartsWith("nested:")).ToArray();
        Assert.Equal(8, nested.Length);
        Assert.Equal("NEAREST_REQUIRED_RULE", nested[^1].Content);
        Assert.Equal("ancestor 2", nested[0].Content);
        Assert.Contains(result.Skips, skip => skip.Reason == "directory_limit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compacted_file_observations_restore_scopes_without_native_snapshots(bool warmCache)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var directory = Path.Combine(fixture.RootPath, "src");
        Directory.CreateDirectory(directory);
        var instructions = Path.Combine(directory, "AGENTS.md");
        File.WriteAllText(instructions, "PERSIST_REQUIRED_RULE");
        var initial = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        await fixture.PayloadStore.CaptureBaseContextAsync(initial.Run.RunId, [], BackendHarnessTestFixture.Timestamp, default);
        var ids = new CryptographicIdGenerator();
        using var arguments = JsonDocument.Parse("{\"path\":\"src/file.cs\"}");
        var step = new AssistantStep("Checking " + new string('a', 24000),
            [new ModelToolCall("observed-call", "read", "1.0.0", arguments.RootElement)], AssistantFinishReason.ToolCalls, 0);
        await AppendAsync(HarnessPayloadKind.AssistantStep, HarnessPayloadEncoding.Json, AssistantStepJsonCodec.Serialize(step),
            (id, payload) => new(id, HarnessEventKind.ModelStepCompleted, modelStepId: ids.NewModelStepId(), payloadId: payload));
        await AppendAsync(HarnessPayloadKind.ToolOutput, HarnessPayloadEncoding.Utf8Text, Encoding.UTF8.GetBytes("file contents"),
            (id, payload) => new(id, HarnessEventKind.ToolExecutionCompleted, toolCallId: ids.NewToolCallId(),
                providerCallId: "observed-call", executionId: ids.NewExecutionId(), payloadId: payload,
                safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false));
        await AppendAsync(HarnessPayloadKind.UserPrompt, HarnessPayloadEncoding.Utf8Text, Encoding.UTF8.GetBytes("Continue"),
            (id, payload) => new(id, HarnessEventKind.RunNoteAdded, payloadId: payload));
        var assembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var provider = new RuPromptContextProvider(fixture.RootPath, new RuInstructionLoader(localApplicationDataRoot: fixture.RootPath),
            new StubSettingsStore(Settings()), new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var before = await assembler.AssembleAsync(initial.Run.RunId, default);
        Assert.DoesNotContain(before.Entries, RuntimeContextSnapshot.IsSnapshot);
        Assert.Contains((await provider.BuildAsync(TestRun(), before, default)).RuntimeContext.References,
            reference => reference.Kind == "nested_instructions" && reference.Content.Contains("PERSIST_REQUIRED_RULE"));
        var plan = Assert.IsType<ContextCompactionPlan>(ContextCompactionPlanner.Plan(before,
            new ContextBudgetPolicy().Evaluate(10000, 5000, ContextCompactionStrategy.Normal), "scope-compact", ContextCompactionReason.Manual));
        Assert.Contains(plan.CoveredEntries, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        var run = (await fixture.EventStore.GetRunAsync(initial.Run.RunId, default))!;
        var summary = new HarnessPayloadIntent(ids.NewPayloadId(), HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, 1024, Encoding.UTF8.GetBytes("Prior work summarized."));
        Assert.Equal(HarnessCommitOutcome.Committed, (await fixture.EventStore.CommitAsync(new(run.RunId, run.Version,
            Next(run), [new(ids.NewEventId(), HarnessEventKind.ContextCompacted)], [summary], [],
            new ContextCompactionIntent(run.RunId, plan.CompactionId, summary.PayloadId, plan.Strategy, plan.Reason,
                plan.CoveredRanges, plan.BeforeTokens, plan.AfterTokens, plan.InputHash)), default)).Outcome);
        if (!warmCache) assembler = new(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var restored = await assembler.AssembleAsync(run.RunId, default);
        Assert.DoesNotContain(restored.Entries, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        Assert.DoesNotContain(restored.Entries, RuntimeContextSnapshot.IsSnapshot);
        File.WriteAllText(instructions, "REFRESHED_REQUIRED_RULE");
        var refreshed = await provider.BuildAsync(TestRun(), restored, default);
        Assert.Contains(refreshed.RuntimeContext.References, reference => reference.Kind == "nested_instructions"
            && reference.Content.Contains("REFRESHED_REQUIRED_RULE"));
        File.Delete(instructions);
        var removed = await provider.BuildAsync(TestRun(), restored, default);
        Assert.Contains(removed.RuntimeContext.References, reference => reference.Kind == "nested_instructions"
            && reference.Content.Contains("No nested instructions"));

        AgentRun Next(AgentRun current) => new(current.RunId, current.SessionId, current.WorkspaceId, current.Status,
            current.Version + 1, current.LastSequence + 1, current.BudgetWindow, current.ModelStepsInWindow,
            current.ToolCallsInWindow, current.Preset, current.PolicySnapshotVersion, current.PolicySnapshotSha256,
            current.CreatedAt, DateTimeOffset.UtcNow);
        async Task AppendAsync(HarnessPayloadKind kind, HarnessPayloadEncoding encoding, byte[] bytes,
            Func<EventId, PayloadId, HarnessEventIntent> makeEvent)
        {
            var current = (await fixture.EventStore.GetRunAsync(initial.Run.RunId, default))!;
            var payload = new HarnessPayloadIntent(ids.NewPayloadId(), kind,
                HarnessPayloadAudience.ModelContext | HarnessPayloadAudience.AuthenticatedClient, encoding, bytes.Length, bytes);
            Assert.Equal(HarnessCommitOutcome.Committed, (await fixture.EventStore.CommitAsync(new(current.RunId, current.Version,
                Next(current), [makeEvent(ids.NewEventId(), payload.PayloadId)], [payload], []), default)).Outcome);
        }
    }
}
