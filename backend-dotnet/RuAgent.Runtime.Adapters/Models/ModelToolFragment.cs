namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelToolFragment
{
    public ModelToolFragment(string? providerCallId, string toolName, string argumentsFragment)
    {
        if (providerCallId is not null && (providerCallId.Length == 0 || providerCallId.Length > 256))
        {
            throw new ArgumentException("Provider call identifier exceeds the adapter cap.", nameof(providerCallId));
        }

        if (toolName is null || toolName.Length > 128 || toolName.Contains('\0'))
        {
            throw new ArgumentException("Tool name exceeds the adapter cap.", nameof(toolName));
        }

        if (argumentsFragment is null || argumentsFragment.Length > 64 * 1024 || argumentsFragment.Contains('\0'))
        {
            throw new ArgumentException("Tool argument fragment exceeds the adapter cap.", nameof(argumentsFragment));
        }

        ProviderCallId = providerCallId;
        ToolName = toolName;
        ArgumentsFragment = argumentsFragment;
    }

    public string? ProviderCallId { get; }

    public string ToolName { get; }

    public string ArgumentsFragment { get; }
}
