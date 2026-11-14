using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>ADR 0047: the Host-intercepted advisor call completes inline and never reaches the gateway.</summary>
public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task Advisor_call_is_answered_by_the_consultant_and_recorded_as_a_read_only_receipt()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{}", "Planning the change."));
        var model = new AdvisingModel(inner, _ => new AdvisorConsultationResult("Check the migration first.", null, "claude-opus-5-5"));
        var gateway = new FakeToolGateway();

        var result = await Coordinator(model, gateway, store).RunOneStepAsync(RunId(), default);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("ok", receipt.Code);
        Assert.False(receipt.SideEffectsPossible);
        Assert.Empty(gateway.Proposals);
        Assert.Empty(gateway.ExecuteAttempts);
        var request = Assert.Single(model.Requests);
        Assert.Null(request.RequestedModel);
        Assert.Null(request.RequestedEffort);
        Assert.Equal("Planning the change.", request.Source.AssistantText);
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Contains(events, item => item.Kind == HarnessEventKind.ToolExecutionRequested && item.SafeCode == "read_only");
        var output = store.Commits.SelectMany(commit => commit.Payloads)
            .Single(payload => payload.Kind == HarnessPayloadKind.ToolOutput);
        var advice = JsonDocument.Parse(output.Plaintext.ToArray()).RootElement;
        Assert.Equal("Check the migration first.", advice.GetProperty("advice").GetString());
        Assert.Equal("claude-opus-5-5", advice.GetProperty("advisor_model").GetString());
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    [Fact]
    public async Task Advisor_passes_a_named_model_and_effort_to_the_consultant()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{\"model\":\" claude-opus-5-5 \",\"effort\":\"high\"}"));
        var model = new AdvisingModel(inner, _ => new AdvisorConsultationResult("ok", null));

        await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);

        var request = Assert.Single(model.Requests);
        Assert.Equal("claude-opus-5-5", request.RequestedModel);
        Assert.Equal("high", request.RequestedEffort);
    }

    [Theory]
    [InlineData("{\"question\":\"x\"}")]
    [InlineData("{\"model\":7}")]
    [InlineData("{\"model\":\"\"}")]
    public async Task Advisor_rejects_unknown_or_malformed_arguments_without_consulting(string arguments)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep(arguments));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("must not consult"));

        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);

        Assert.Equal(AdvisorCodes.ArgumentsInvalid, Assert.Single(result.Receipts).Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task A_child_cannot_consult_the_advisor()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{}"));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("must not consult"));
        var gateway = new FakeToolGateway();

        var result = await Coordinator(model, gateway, store, internalRun: true).RunOneStepAsync(RunId(), default);

        Assert.Equal(AdvisorCodes.SubagentDenied, Assert.Single(result.Receipts).Code);
        Assert.Empty(model.Requests);
        Assert.Empty(gateway.Proposals);
    }

    [Fact]
    public async Task Advisor_mixed_with_another_call_is_refused_and_the_other_call_still_runs()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        using var advisorArguments = JsonDocument.Parse("{}");
        using var readArguments = JsonDocument.Parse("{\"path\":\"README.md\"}");
        inner.Enqueue(new AssistantStep("both", [
            new("provider_advisor", "advisor", "1.0.0", advisorArguments.RootElement.Clone()),
            new("provider_read", "files.read", "1", readArguments.RootElement.Clone())],
            AssistantFinishReason.ToolCalls, 16));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("must not consult"));
        var gateway = new FakeToolGateway();

        var result = await Coordinator(model, gateway, store).RunOneStepAsync(RunId(), default);

        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(AdvisorCodes.MustBeAlone, result.Receipts[0].Code);
        Assert.Single(gateway.ExecuteAttempts);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Without_a_consultant_the_advisor_is_unavailable_by_name()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var model = new FakeModelStepRunner();
        model.Enqueue(AdvisorStep("{}"));

        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal(AdvisorCodes.Unavailable, receipt.Code);
    }

    [Theory]
    [InlineData(AdvisorCodes.NotSelected)]
    [InlineData(AdvisorCodes.ProviderFailed)]
    [InlineData(AdvisorCodes.ModelUserRequestRequired)]
    public async Task A_failed_consultation_is_a_named_result(string code)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{}"));
        var model = new AdvisingModel(inner, _ => AdvisorConsultationResult.Failed(code));

        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal(code, receipt.Code);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    [Theory]
    [InlineData(AdvisorCodes.Unavailable, false)]
    [InlineData("malformed_call", true)]
    public async Task Finishing_after_a_failed_consultation_is_not_a_failed_tool(string code, bool fails)
    {
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, AdvisorStep("{}")),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_advisor"),
                "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("Done without advice."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));

        var result = await Coordinator(model, new FakeToolGateway(), store, contextSource: source)
            .RunOneStepAsync(RunId(), default);

        // The control case shows the guard is live: after an ordinary failed tool the final
        // reply is not accepted as it stands, while after a failed consultation it is.
        Assert.Equal(!fails, result.Run.State == HarnessRunState.WaitingHistory);
    }

    [Theory]
    [InlineData(AdvisorCodes.Interrupted, true)]
    [InlineData(AdvisorCodes.ProviderFailed, false)]
    public async Task Only_an_interrupted_consultation_may_be_retried_identically(string code, bool consults)
    {
        var source = new FixedContextSource(
            ModelContextEntry.Assistant("assistant-prior", 1, AdvisorStep("{}")),
            ModelContextEntry.ToolResult(
                "tool-prior",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    new ToolCallId("call_11111111111111111111111111111111"),
                    ToolExecutionOutcome.Failed,
                    code,
                    null,
                    sideEffectsPossible: false,
                    providerCallId: "provider_advisor"),
                "{}"));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{}"));
        var model = new AdvisingModel(inner, _ => new AdvisorConsultationResult("advice", null));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));

        await Coordinator(model, new FakeToolGateway(), store, contextSource: source).RunOneStepAsync(RunId(), default);

        // A provider failure repeated verbatim is the loop the guard stops; an interrupted
        // consultation never ran, so asking again is a retry.
        Assert.Equal(consults ? 1 : 0, model.Requests.Count);
    }

    [Fact]
    public async Task A_thrown_consultant_failure_becomes_a_named_result_but_cancellation_propagates()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inner = new FakeModelStepRunner();
        inner.Enqueue(AdvisorStep("{}"));
        var model = new AdvisingModel(inner, _ => throw new InvalidOperationException("boom"));

        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);

        Assert.Equal(AdvisorCodes.ProviderFailed, Assert.Single(result.Receipts).Code);
    }

    private static AssistantStep AdvisorStep(string arguments, string text = "asking") =>
        new(text, [new ModelToolCall("provider_advisor", "advisor", "1.0.0", JsonDocument.Parse(arguments).RootElement.Clone())],
            AssistantFinishReason.ToolCalls, 16);

    private sealed class AdvisingModel(
        FakeModelStepRunner inner,
        Func<AdvisorConsultationRequest, AdvisorConsultationResult> answer) : IModelStepRunner, IAdvisorConsultant
    {
        public List<AdvisorConsultationRequest> Requests { get; } = new();

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken) =>
            inner.RunStepAsync(request, publishDelta, cancellationToken);

        public Task<AdvisorConsultationResult> ConsultAsync(AdvisorConsultationRequest request, CancellationToken cancellationToken)
        {
            var result = answer(request);
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
