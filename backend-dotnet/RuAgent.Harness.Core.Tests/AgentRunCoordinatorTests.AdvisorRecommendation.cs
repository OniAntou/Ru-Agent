using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Harness.Core.Tests.Prompting;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// ADR 0047: with an advisor selected, a root run about to finish is recommended once to consult
/// it. The model decides; the Host never consults on its behalf.
/// </summary>
public sealed partial class AgentRunCoordinatorTests
{
    private const string ReadCall = """{"path":"pricing.py"}""";

    [Fact]
    public async Task Finished_tool_work_the_advisor_has_not_seen_is_recommended_once_then_a_repeated_reply_completes()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(FinalStep("Done: the discount is applied."));
        inner.Enqueue(FinalStep("Done: the discount is applied."));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("the Host must not consult"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var coordinator = AdvisedCoordinator(model, store, source);

        var held = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.Running, held.Run.State);
        Assert.Empty(model.Requests);
        var commit = store.Commits.Last();
        Assert.Equal("advisor_recommended", Assert.Single(commit.Events, item => item.Kind == HarnessEventKind.RunNoteAdded).SafeCode);
        var note = Encoding.UTF8.GetString(Assert.Single(commit.Payloads, payload => payload.Kind == HarnessPayloadKind.ModelContextNote).Plaintext.Span);
        Assert.StartsWith($"{AgentRecoveryNotes.NotePrefix} advisor_recommended", note, StringComparison.Ordinal);
        Assert.Contains("not a requirement", note, StringComparison.Ordinal);
        Assert.Contains("give your final reply again", note, StringComparison.Ordinal);
        Assert.Empty(store.Commits.SelectMany(item => item.ConversationOutbox));
        AppendNote(source, commit.Payloads, sequence: 3);

        var declined = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.WaitingHistory, declined.Run.State);
        Assert.Single(store.Commits.SelectMany(item => item.Events), item => item.SafeCode == "advisor_recommended");
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task A_consultation_after_the_last_tool_work_needs_no_recommendation()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));
        AppendEntries(source, VerificationCompletionTests.Batch(3, "advisor-3", "advisor", "{}",
            ToolExecutionOutcome.Succeeded, "ok", """{"advice":"Looks right."}"""));
        var result = await RunFinalAsync(source, recommendAdvisor: true);

        Assert.Equal(HarnessRunState.WaitingHistory, result.State);
    }

    [Fact]
    public async Task Tool_work_after_a_consultation_is_recommended_again_for_review()
    {
        var source = new FixedContextSource();
        AppendEntries(source, VerificationCompletionTests.Batch(1, "advisor-1", "advisor", "{}",
            ToolExecutionOutcome.Succeeded, "ok", """{"advice":"Read pricing.py first."}"""));
        AppendEntries(source, ReadEntries(3));

        var result = await RunFinalAsync(source, recommendAdvisor: true);

        Assert.Equal(HarnessRunState.Running, result.State);
    }

    [Fact]
    public async Task A_failed_consultation_still_counts_as_asked()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));
        AppendEntries(source, VerificationCompletionTests.Batch(3, "advisor-3", "advisor", "{}",
            ToolExecutionOutcome.Failed, AdvisorCodes.ProviderFailed, "{}"));

        var result = await RunFinalAsync(source, recommendAdvisor: true);

        Assert.Equal(HarnessRunState.WaitingHistory, result.State);
    }

    [Fact]
    public async Task A_run_without_tool_work_is_not_recommended()
    {
        var result = await RunFinalAsync(new FixedContextSource(), recommendAdvisor: true);

        Assert.Equal(HarnessRunState.WaitingHistory, result.State);
    }

    [Fact]
    public async Task A_run_whose_user_selected_no_advisor_is_not_recommended()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));

        var result = await RunFinalAsync(source, recommendAdvisor: false);

        Assert.Equal(HarnessRunState.WaitingHistory, result.State);
    }

    [Fact]
    public async Task A_step_that_does_not_advertise_the_advisor_is_not_recommended()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));

        var result = await RunFinalAsync(source, recommendAdvisor: true, advertiseAdvisor: false);

        Assert.Equal(HarnessRunState.WaitingHistory, result.State);
    }

    [Fact]
    public async Task A_child_run_is_never_recommended()
    {
        var source = new FixedContextSource();
        AppendEntries(source, ReadEntries(1));

        var result = await RunFinalAsync(source, recommendAdvisor: true, internalRun: true);

        Assert.NotEqual(HarnessRunState.Running, result.State);
    }

    private async Task<AgentRun> RunFinalAsync(
        FixedContextSource source, bool recommendAdvisor, bool advertiseAdvisor = true, bool internalRun = false)
    {
        var inner = new FakeModelStepRunner();
        inner.Enqueue(FinalStep("Done."));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("the Host must not consult"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var coordinator = AdvisedCoordinator(model, store, source, recommendAdvisor, advertiseAdvisor, internalRun);

        var result = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Empty(model.Requests);
        Assert.Equal(result.Run.State == HarnessRunState.Running,
            store.Commits.SelectMany(item => item.Events).Any(item => item.SafeCode == "advisor_recommended"));
        return result.Run;
    }

    private static AgentRunCoordinator AdvisedCoordinator(
        IModelStepRunner model, InMemoryHarnessEventStore store, FixedContextSource source,
        bool recommendAdvisor = true, bool advertiseAdvisor = true, bool internalRun = false) =>
        Coordinator(model, new FakeToolGateway(), store, contextSource: source, internalRun: internalRun,
            recommendAdvisor: recommendAdvisor,
            promptContextProvider: new FakeSystemPromptContextProvider((_, _) =>
                advertiseAdvisor ? PromptContextWithAdvisor() : SystemPromptContext.Empty));

    private static SystemPromptContext PromptContextWithAdvisor()
    {
        var definition = AgentToolDefinitionCatalog.Find(AdvisorCodes.ToolId, "1.0.0")!;
        var advisor = new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled");
        var empty = SystemPromptContext.Empty;
        return new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, SystemPromptContext.RequiredAgentProfile,
            empty.Preset, empty.Environment, [], [advisor], empty.RuntimeContext);
    }

    private static ModelContextEntry[] ReadEntries(long sequence) =>
        VerificationCompletionTests.Batch(sequence, "read-" + sequence, "read", ReadCall,
            ToolExecutionOutcome.Succeeded, "ok", "def total_price(items, discount_percent): ...");
}
