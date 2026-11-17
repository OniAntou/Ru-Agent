using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>Native continuation of the preceding root run (ADR 0049).</summary>
public sealed partial class SessionRunHandoffStoreTests
{
    [Fact]
    public async Task A_larger_budget_renders_more_of_the_same_history()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        for (int number = 1; number <= 3; number++)
        {
            var request = await CreateAsync(fixture, number, 1, 1);
            await EndAsync(fixture, request, number, "result-" + number, recoveryNotes: 40);
        }
        var target = await CreateAsync(fixture, 4, 1, 1);

        var small = await fixture.PayloadStore.ReadSessionRunHandoffContextAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        var large = await fixture.PayloadStore.ReadSessionRunHandoffContextAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default, maximumCharacters: 120_000);

        Assert.True(small!.RenderedHandoff.Length <= SessionRunHandoff.DefaultCharacters);
        Assert.True(large!.RenderedHandoff.Length <= 120_000);
        Assert.True(large.RenderedHandoff.Length > small.RenderedHandoff.Length * 3,
            $"small={small.RenderedHandoff.Length} large={large.RenderedHandoff.Length}");
    }

    [Fact]
    public async Task A_native_source_supplies_scope_hints_but_is_not_rendered()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "older-result");
        var newest = await CreateAsync(fixture, 2, 1, 1);
        await EndAsync(fixture, newest, 2, "newest-result", observedFilePath: "src/Parser.cs");
        var target = await CreateAsync(fixture, 3, 1, 1);

        var context = await fixture.PayloadStore.ReadSessionRunHandoffContextAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default, nativeSourceRunId: newest.Run.RunId);

        Assert.Contains("older-result", context!.RenderedHandoff);
        Assert.DoesNotContain("newest-result", context.RenderedHandoff);
        Assert.Contains(Path.Combine("src", ".ru-instruction-scope"), context.ObservedPaths);
    }

    [Fact]
    public async Task The_candidate_is_the_newest_visible_run_unless_it_overflowed_its_context()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await TerminalAsync(fixture, 1, 1, 1, "older-result");
        var newest = await TerminalAsync(fixture, 2, 1, 1, "newest-result");
        var target = await CreateAsync(fixture, 3, 1, 1);

        var candidate = await fixture.PayloadStore.ReadNativeContinuationCandidateAsync(
            target.Run.RunId, target.Run.SessionId, VisibleRunIds, default);
        Assert.Equal(newest.Run.RunId, candidate!.RunId);

        // A rewound conversation no longer shows the newest run: it cannot be continued.
        var withoutNewest = VisibleRunIds.Where(id => id != newest.Run.RunId.Value).ToArray();
        var rewound = await fixture.PayloadStore.ReadNativeContinuationCandidateAsync(
            target.Run.RunId, target.Run.SessionId, withoutNewest, default);
        Assert.NotEqual(newest.Run.RunId, rewound!.RunId);

        // A second session, since the first still holds its live target run.
        var overflowed = await CreateAsync(fixture, 4, 2, 1);
        await EndAsync(fixture, overflowed, 4, "too-large", terminalErrorCode: "context_limit_reached");
        var next = await CreateAsync(fixture, 5, 2, 1);
        Assert.Null(await fixture.PayloadStore.ReadNativeContinuationCandidateAsync(
            next.Run.RunId, next.Run.SessionId, VisibleRunIds, default));
    }

    [Fact]
    public async Task A_natively_continued_run_reads_the_source_after_its_base_and_hides_the_marker()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var source = await CreateAsync(fixture, 1, 1, 1);
        await CaptureEmptyBaseAsync(fixture, source);
        await EndAsync(fixture, source, 1, "native-evidence");
        var target = await CreateAsync(fixture, 2, 1, 1);
        await fixture.PayloadStore.CaptureBaseContextAsync(target.Run.RunId,
        [
            new(HarnessStoreTestFixture.Payload(990), "base:turn:1:user", 1, ModelContextRole.User, "earlier turn"),
            new(HarnessStoreTestFixture.Payload(991), NativeRunContinuation.MarkerSourceId, 2, ModelContextRole.User,
                NativeRunContinuation.EncodeMarker(source.Run.RunId)),
        ], HarnessStoreTestFixture.Timestamp, default);

        var reader = new SqliteModelContextSource(fixture.Options, fixture.PayloadStore);
        var snapshot = await reader.LoadAsync(target.Run.RunId, default);

        Assert.Equal(source.Run.RunId, await fixture.PayloadStore.ReadNativeContinuationSourceAsync(target.Run.RunId, default));
        Assert.DoesNotContain(snapshot.SessionBase, entry => entry.SourceId == NativeRunContinuation.MarkerSourceId);
        Assert.Equal("earlier turn", snapshot.SessionBase[0].Content);
        Assert.Contains(snapshot.SessionBase, entry => entry.Content == "original-request-1");
        var step = Assert.Single(snapshot.SessionBase, entry => entry.Kind == ModelContextEntryKind.AssistantStep);
        var result = Assert.Single(snapshot.SessionBase, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        Assert.Equal("native-evidence", result.Content);
        Assert.Equal(step.AssistantStep!.ToolCalls[0].ProviderCallId, result.ToolReceipt!.ProviderCallId);
        Assert.True(step.Sequence > 2 && result.Sequence > step.Sequence);
        // The run's own prompt stays run-local, after the carried history.
        Assert.Equal("original-request-2", Assert.Single(snapshot.RunLocal).Content);

        // A cold reader (as after a restart) rebuilds the same carried entries.
        var reopened = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
            .LoadAsync(target.Run.RunId, default);
        Assert.Equal(snapshot.SessionBase.Select(entry => (entry.SourceId, entry.Sequence)),
            reopened.SessionBase.Select(entry => (entry.SourceId, entry.Sequence)));
    }

    [Fact]
    public async Task An_unsettled_batch_of_the_source_becomes_a_note_instead_of_history()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var source = await CreateAsync(fixture, 1, 1, 1);
        await CaptureEmptyBaseAsync(fixture, source);
        await EndAsync(fixture, source, 1, "settled-result", endsWithUnsettledCall: true);

        var slice = await fixture.PayloadStore.ReadNativeContinuationSliceAsync(source.Run.RunId, default);

        Assert.Contains(slice.Entries, entry => entry.Content == "settled-result");
        Assert.DoesNotContain(slice.Entries, entry => entry.AssistantStep?.ToolCalls
            .Any(call => call.ProviderCallId == "call-open-1") == true);
        Assert.Contains("call=call-open-1 receipt=none", slice.UnsettledNote);
        Assert.Contains("unverified", slice.UnsettledNote);
    }

    [Fact]
    public async Task Carried_history_is_compacted_like_the_run_own_and_the_run_still_reads_as_a_source()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var source = await CreateAsync(fixture, 1, 1, 1);
        await CaptureEmptyBaseAsync(fixture, source);
        await EndAsync(fixture, source, 1, "carried-result " + new string('r', 6000), recoveryNotes: 3);
        var target = await CreateAsync(fixture, 2, 1, 1);
        await fixture.PayloadStore.CaptureBaseContextAsync(target.Run.RunId,
            [new(HarnessStoreTestFixture.Payload(991), NativeRunContinuation.MarkerSourceId, 1, ModelContextRole.User,
                NativeRunContinuation.EncodeMarker(source.Run.RunId))],
            HarnessStoreTestFixture.Timestamp, default);
        var warm = new SqliteModelContextSource(fixture.Options, fixture.PayloadStore);
        var context = await new AgentContextAssembler(warm).AssembleAsync(target.Run.RunId, default);

        // The real planner selects carried entries: they keep their event ids, not a base prefix.
        var request = ModelContextSizeEstimator.EstimateTokens(context);
        var usable = request * 4 / 10;
        var plan = ContextCompactionPlanner.Plan(context,
            new ContextBudgetDecision(request, usable * 2, usable, usable, usable * 62 / 100, true,
                ContextCompactionStrategy.Normal),
            "compact-carried", ContextCompactionReason.Automatic, estimatedTokensOverride: request,
            retainRecentTokens: 0);
        Assert.NotNull(plan);
        Assert.Contains(plan.CoveredEntries, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        await CompactAsync(fixture, target, "compact-carried", "summary of carried work", 1, [], plan.CoveredRanges);

        foreach (var reader in new[] { warm, new SqliteModelContextSource(fixture.Options, fixture.PayloadStore) })
        {
            var compacted = await reader.LoadAsync(target.Run.RunId, default);
            Assert.Contains(compacted.SessionBase, entry => entry.Content == "summary of carried work");
            Assert.DoesNotContain(compacted.SessionBase, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        }

        // Read as the next run's source, its summary of carried entries is skipped, not fatal.
        await EndAsync(fixture, target, 2, "target-result");
        var slice = await fixture.PayloadStore.ReadNativeContinuationSliceAsync(target.Run.RunId, default);
        Assert.Contains(slice.Entries, entry => entry.Content == "target-result");
        Assert.DoesNotContain(slice.Entries, entry => entry.Content?.StartsWith("carried-result", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_run_without_a_marker_reads_no_source()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var target = await CreateAsync(fixture, 1, 1, 1);

        Assert.Null(await fixture.PayloadStore.ReadNativeContinuationSourceAsync(target.Run.RunId, default));
    }

    // Every root run captures its base before it runs; a source run is read through it.
    private static Task CaptureEmptyBaseAsync(HarnessStoreTestFixture fixture, NewAgentRun request) =>
        fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
}
