using System.Collections.ObjectModel;
using RuAgent.Harness.Core.Workspace;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicySnapshot
{
    public PolicySnapshot(
        long version,
        AgentPermissionPreset preset,
        WorkspaceIdentity workspace,
        IReadOnlyList<PolicyRule> orderedRules,
        DateTimeOffset createdAt,
        string sha256)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(orderedRules);
        ArgumentNullException.ThrowIfNull(sha256);

        Version = version;
        Preset = preset;
        Workspace = workspace;
        OrderedRules = new ReadOnlyCollection<PolicyRule>(orderedRules.ToArray());
        CreatedAt = createdAt;
        Sha256 = sha256;
    }

    public long Version { get; }

    public AgentPermissionPreset Preset { get; }

    public WorkspaceIdentity Workspace { get; }

    public IReadOnlyList<PolicyRule> OrderedRules { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Sha256 { get; }
}
