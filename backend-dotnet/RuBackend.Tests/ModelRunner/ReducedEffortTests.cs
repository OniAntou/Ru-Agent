using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using RuBackend.Providers;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.ModelRunner;

public sealed class ReducedEffortTests
{
    private static JsonObject Effort(string value) => new() { ["reasoning_effort"] = value };

    private static CatalogModel Model(params string[] efforts) =>
        new("m", true, "chat_completions", new CatalogReasoning("supported", ["low", "high"]))
        {
            WireOptions = efforts.ToDictionary(effort => effort, effort => Effort(effort)),
        };

    private static ProviderCatalogSnapshot Snapshot(CatalogModel model) =>
        new("ok", "b", "i", "m", "default", ["m"], [model], false, null, null);

    [Fact]
    public void Lowest_effort_is_chosen_by_rank_not_by_declaration_order()
    {
        var reduced = ProviderReasoningPreferences.LowestEffortOptions(
            Snapshot(Model("high", "medium", "low", "xhigh")), "m", new JsonObject());

        Assert.Equal("low", reduced!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void No_reduction_when_already_lowest_or_the_model_offers_no_effort()
    {
        Assert.Null(ProviderReasoningPreferences.LowestEffortOptions(Snapshot(Model("low", "high")), "m", Effort("low")));
        Assert.Null(ProviderReasoningPreferences.LowestEffortOptions(Snapshot(Model()), "m", new JsonObject()));
        Assert.Null(ProviderReasoningPreferences.LowestEffortOptions(Snapshot(Model("low")), "other", new JsonObject()));
        Assert.Null(ProviderReasoningPreferences.LowestEffortOptions(Snapshot(Model("custom-level")), "m", new JsonObject()));
    }

    [Fact]
    public void WithReducedEffort_swaps_the_options_without_touching_the_original()
    {
        var original = Bootstrap(Effort("high"), Effort("low"));

        var reduced = original.WithReducedEffort();

        Assert.Equal("low", reduced.RequestOptions!["reasoning_effort"]!.GetValue<string>());
        Assert.Null(reduced.ReducedEffortRequestOptions);
        Assert.Equal("high", original.RequestOptions!["reasoning_effort"]!.GetValue<string>());
        var plain = Bootstrap(Effort("high"), null);
        Assert.Same(plain, plain.WithReducedEffort());
    }

    [Fact]
    public async Task A_truncation_note_in_context_makes_the_next_request_use_the_reduced_effort()
    {
        var provider = new CapturingProvider();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap(Effort("high"), Effort("low"))),
        };

        await runner.RunStepAsync(Request(withTruncationNote: false), _ => ValueTask.CompletedTask, CancellationToken.None);
        await runner.RunStepAsync(Request(withTruncationNote: true), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("high", provider.Requests[0].Settings["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("low", provider.Requests[1].Settings["reasoning_effort"]!.GetValue<string>());
    }

    private static ProviderBootstrapData Bootstrap(JsonObject? options, JsonObject? reduced) => new(
        "Kilo", "chat_completions", new Uri("https://step-provider.invalid/v1"), "model-b", "step-secret",
        requestOptions: options, contextWindowTokens: 32_768, reducedEffortRequestOptions: reduced);

    private static ModelStepRequest Request(bool withTruncationNote)
    {
        var entries = new List<ModelContextEntry> { ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt") };
        if (withTruncationNote)
            entries.Add(ModelContextEntry.Text("note-1", 2, ModelContextRole.System, AgentRecoveryNotes.BuildTruncatedNote()));
        return new ModelStepRequest(
            new RuAgent.Harness.Core.RunId("run_" + new string('a', 32)),
            new ModelStepId("step_" + new string('b', 32)),
            new ModelContextSnapshot(entries, Array.Empty<byte>()),
            SystemPromptContext.Empty,
            RuAgent.Protocol.Harness.AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('c', 64));
    }

    private sealed class CapturingProvider : IModelProvider, IModelRequestBodyProjector
    {
        public List<ModelRequest> Requests { get; } = [];

        public string ProjectRequestBody(ModelRequest request) =>
            new JsonObject { ["model"] = request.Model, ["messages"] = new JsonArray() }.ToJsonString();

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelDelta("ok", [], completed: true, finishReason: "stop");
        }
    }
}
