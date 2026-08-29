using System.Collections.Generic;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;

namespace RuAgent.Runtime.Worker;

public interface IWorkerV1RequestRouter
{
    IAsyncEnumerable<WorkerV1Reply> RouteAsync(
        string type,
        JsonObject payload,
        CancellationToken cancellationToken);
}

public sealed class UnavailableWorkerV1RequestRouter : IWorkerV1RequestRouter
{
    public async IAsyncEnumerable<WorkerV1Reply> RouteAsync(
        string type,
        JsonObject payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var code = type switch
        {
            WorkerV1Contracts.RuntimeLoad or WorkerV1Contracts.RuntimeRequest => "runtime_unavailable",
            WorkerV1Contracts.VoiceStart => "voice_unavailable",
            WorkerV1Contracts.VtsCatalog or WorkerV1Contracts.VtsAction => "vts_unavailable",
            _ => "unsupported_operation",
        };
        yield return new WorkerV1Reply(
            WorkerV1Contracts.Error,
            new JsonObject
            {
                ["code"] = code,
                ["retryable"] = true,
            });
    }
}
