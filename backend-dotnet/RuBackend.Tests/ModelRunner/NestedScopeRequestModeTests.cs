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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instruction_scope_metadata_survives_request_preparation_without_native_text_snapshots(bool withImage)
    {
        var provider = new ScriptedProvider((_, _) => Deltas(new ModelDelta("answer", [], true, "stop")));
        var bootstrap = Bootstrap();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(new ProviderBootstrapData(bootstrap.Backend,
                withImage ? bootstrap.Protocol : "openai_responses", bootstrap.Endpoint, bootstrap.Model, bootstrap.Credential,
                toolCallingMode: withImage ? ProviderToolCallingMode.Native : ProviderToolCallingMode.TextSelection,
                contextWindowTokens: bootstrap.ContextWindowTokens)),
            LoadRunImagesAsync = (_, _) => Task.FromResult<IReadOnlyList<string>>(withImage ? ["data:image/png;base64,aGVsbG8="] : []),
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
