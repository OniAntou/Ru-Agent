using System.Collections.ObjectModel;
using RuAgent.Harness.Core.Workspace;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicySnapshot
{
    public PolicySnapshot(
        long Version,
        AgentPermissionPreset Preset,
        WorkspaceIdentity Workspace,
        IReadOnlyList<PolicyRule> OrderedRules,
        DateTimeOffset CreatedAt,
        string Sha256)
    {
        if (Version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Version));
        }

        if (!Enum.IsDefined(Preset))
        {
            throw new ArgumentOutOfRangeException(nameof(Preset));
        }

        ArgumentNullException.ThrowIfNull(Workspace);
        ArgumentNullException.ThrowIfNull(OrderedRules);
        ArgumentNullException.ThrowIfNull(Sha256);

        if (CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt must be a valid timestamp.", nameof(CreatedAt));
        }

        if (Sha256.Length != 0 && !IsSha256(Sha256))
        {
            throw new ArgumentException("Sha256 must be empty or exactly 64 lowercase hex characters.", nameof(Sha256));
        }

        foreach (var rule in OrderedRules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            rule.Validate();
        }

        this.Version = Version;
        this.Preset = Preset;
        this.Workspace = Workspace;
        this.OrderedRules = new ReadOnlyCollection<PolicyRule>(OrderedRules.ToArray());
        this.CreatedAt = CreatedAt;
        this.Sha256 = Sha256;
    }

    public long Version { get; }

    public AgentPermissionPreset Preset { get; }

    public WorkspaceIdentity Workspace { get; }

    public IReadOnlyList<PolicyRule> OrderedRules { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Sha256 { get; }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
