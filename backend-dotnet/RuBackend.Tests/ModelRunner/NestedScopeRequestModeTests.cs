using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed partial class InProcessModelStepRunnerTests
{
    // Attached images keep the prompt out of history, so no runtime snapshot is prepared.
    // (The text-selection variant of this case retired with that calling mode.)
    [Fact]
    public async Task Instruction_scope_metadata_survives_request_preparation_without_native_text_snapshots()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(new ModelDelta("answer", [], true, "stop")));
        var bootstrap = Bootstrap();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(new ProviderBootstrapData(bootstrap.Backend,
                bootstrap.Protocol, bootstrap.Endpoint, bootstrap.Model, bootstrap.Credential,
                contextWindowTokens: bootstrap.ContextWindowTokens)),
            LoadRunImagesAsync = (_, _) => Task.FromResult<IReadOnlyList<string>>(["data:image/png;base64,aGVsbG8="]),
        };
        var original = Request();
        var context = new ModelContextSnapshot(original.Context.Entries, original.Context.CanonicalUtf8, ["src/file.cs"]);
        var request = new ModelStepRequest(original.RunId, original.ModelStepId, context, original.PromptContext,
            original.Preset, original.PolicySnapshotVersion, original.PolicySnapshotSha256);
        var prepared = await runner.PrepareAsync(request, default);
        Assert.Null(prepared.RuntimeContextSnapshot);
        Assert.Equal("src/file.cs", Assert.Single(request.Context.ObservedFilePaths));
        await runner.RunStepAsync(request.WithPreparation(prepared), _ => ValueTask.CompletedTask, default);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain("src/file.cs", string.Join("\n", provider.Requests[0].Turns.Select(turn => turn.Content)));
    }
}
