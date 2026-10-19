using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using EventId = RuAgent.Harness.Core.EventId;

namespace RuBackend.Harness.Subagents;

/// <summary>Prepares durable child attempts; scheduling and execution stay with the shared runtime.</summary>
public sealed class SubagentRunFactory(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteAgentRunProviderSelectionStore selections, HarnessPayloadStore payloads, SqliteSubagentScopeStore scopes,
    IAgentRuntimeRootRegistry roots, ISubagentModelSelectionProvider models, ISubagentWorkspaceFactory workspaces,
    ITimeProvider time) : IDisposable
{
    // Bound workspace preparation memory and serialize duplicate invocations in this Host.
    private readonly SemaphoreSlim _prepare = new(1, 1);

    public async Task<SubagentRecord> CreateAsync(RunId rootId, string invocationId, SubagentSpawnRequest request, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(invocationId)) throw new HarnessStoreException("subagent_arguments_invalid");
        var role = request.Role switch
        {
            "coding" => SubagentRole.Coding,
            "review" => SubagentRole.Review,
            _ => throw new HarnessStoreException("subagent_role_invalid"),
        };
        var brief = Encoding.UTF8.GetBytes(SubagentContextBuilder.Build(role, request.Task, request.Context));
        var requestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(request));
        var identity = Hash(Encoding.UTF8.GetBytes(rootId.Value + ":" + invocationId))[..32];
        var agentId = "agent_" + identity;
        await _prepare.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var parent = await events.GetRunAsync(rootId, ct).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_root_not_found");
            if (parent.State != HarnessRunState.Running || await agents.IsInternalSessionAsync(parent.SessionId, ct).ConfigureAwait(false))
                throw new HarnessStoreException("subagent_root_inactive");
            if (role == SubagentRole.Coding && parent.Preset == AgentPermissionPreset.Plan)
                throw new HarnessStoreException("subagent_role_denied");
            if (!roots.TryGetRoot(rootId, out var parentRoot) || parentRoot is null
                || !roots.TryGetPolicySnapshot(rootId, out var parentPolicy) || parentPolicy is null
                || parentPolicy.Version != parent.PolicySnapshotVersion || parentPolicy.Sha256 != parent.PolicySnapshotSha256)
                throw new HarnessStoreException("subagent_parent_scope_missing");
            var replay = await agents.FindSpawnAsync(rootId, invocationId, agentId, role, requestHash, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                var attempt = await agents.GetLatestAttemptAsync(rootId, agentId, ct).ConfigureAwait(false);
                if (attempt is null || await scopes.GetAsync(attempt.Run.RunId, ct).ConfigureAwait(false) is null
                    || (await selections.GetAsync(attempt.Run.RunId, ct).ConfigureAwait(false))?.Selection is null)
                    throw new HarnessStoreException("subagent_preparation_incomplete");
                return replay;
            }
            AgentRunProviderSelection selection;
            try { selection = await models.ResolveAsync(rootId, role, ct, request.Model, request.ProviderId).ConfigureAwait(false); }
            catch (SubagentConfigurationException e)
            {
                // Selection is resolved before workspace preparation or durable child creation.
                // Report a known refusal, never an uncertain write requiring reconciliation.
                throw new SubagentControlRejectedException(e.Code);
            }
            var workspace = await workspaces.CreateAsync(parent, parentRoot, parentPolicy, agentId, role, ct).ConfigureAwait(false);
            var now = time.UtcNow;
            var child = new AgentRun(new RunId("run_" + identity), new SessionId(identity), workspace.Policy.Workspace.WorkspaceId,
                new(HarnessRunState.Created), 0, 1, 1, 0, 0, parent.Preset, workspace.Policy.Version, workspace.Policy.Sha256, now, now);
            var promptId = new PayloadId("payload_" + identity);
            var create = new NewAgentRun(child,
                [new(new EventId("evt_" + identity), HarnessEventKind.RunStateChanged, payloadId: promptId)],
                [new(promptId, HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Utf8Text, brief.Length, brief)], [], new(Hash(Encoding.UTF8.GetBytes(invocationId)), requestHash, 1));
            var record = await agents.CreateAsync(new(agentId, rootId, invocationId, role, create), ct).ConfigureAwait(false);
            await selections.PinAsync(child.RunId, selection, ct).ConfigureAwait(false);
            await payloads.CaptureBaseContextAsync(child.RunId, [], now, ct).ConfigureAwait(false);
            var scope = new SubagentRuntimeScope(parentRoot, workspace.Root, new(parentPolicy.Version, parentPolicy.Sha256),
                BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(workspace.Policy)).ToJsonString(), workspace.ManifestJson,
                BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(parentPolicy)).ToJsonString());
            await scopes.SaveAsync(rootId, child.RunId, scope, ct).ConfigureAwait(false);
            return record;
        }
        finally { _prepare.Release(); }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose() => _prepare.Dispose();
}
