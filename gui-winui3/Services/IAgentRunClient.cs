using System;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public interface IAgentRunClient
{
    Task<AgentRunModel> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken = default);
    Task<AgentRunModel> GetRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<AgentPayloadModel> GetPayloadAsync(string runId, string payloadId, CancellationToken cancellationToken = default);
    Task TrustAsync(string runId, TrustAgentRunRequest request, CancellationToken cancellationToken = default);
    Task<long> SetSessionPresetAsync(
        string sessionId,
        string preset,
        long expectedVersion,
        bool interactive,
        CancellationToken cancellationToken = default);
    Task CancelAsync(string runId, CancellationToken cancellationToken = default);
    Task ContinueAsync(string runId, CancellationToken cancellationToken = default);
    Task ApproveAsync(string runId, string approvalId, string scope, CancellationToken cancellationToken = default);
    Task DenyAsync(string runId, string approvalId, CancellationToken cancellationToken = default);
    Task SubscribeEventsAsync(
        string runId,
        long afterSequence,
        Func<AgentEventModel, Task> onEvent,
        CancellationToken cancellationToken = default,
        Func<AgentActivityModel, Task>? onActivity = null,
        Func<AgentRunSummaryModel, Task>? onSummary = null,
        Func<AgentAssistantDeltaModel, Task>? onAssistantDelta = null,
        Func<AgentToolOutputModel, Task>? onToolOutput = null);
}
