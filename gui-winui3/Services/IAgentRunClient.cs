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
    Task<DispatchAgentCommandResponse> DispatchCommandAsync(
        string runId,
        string text,
        CancellationToken cancellationToken = default);
    Task ApproveAsync(string runId, string approvalId, string scope, CancellationToken cancellationToken = default);
    Task DenyAsync(string runId, string approvalId, CancellationToken cancellationToken = default);
    Task SubmitInputAsync(string runId, string inputId, string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a steer into the working run (ADR 0056). A client without steering answers as
    /// a run that cannot take one, so the caller queues the text as the next turn instead.
    /// </summary>
    Task<AgentSteerResponse> SteerAsync(string runId, string steerId, string text, CancellationToken cancellationToken = default) =>
        Task.FromException<AgentSteerResponse>(new AgentApiException("run_not_steerable", "steering unavailable"));

    /// <summary>The run's steers and the Host's state for each: pending, delivered or returned.</summary>
    Task<AgentSteerListResponse> ListSteersAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentSteerListResponse(runId, Array.Empty<AgentSteerItem>()));
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
