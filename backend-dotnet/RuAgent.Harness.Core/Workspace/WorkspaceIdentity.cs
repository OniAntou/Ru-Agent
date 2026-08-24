using RuAgent.Harness.Core;

namespace RuAgent.Harness.Core.Workspace;

public sealed record WorkspaceIdentity
{
    public WorkspaceIdentity(
        WorkspaceId workspaceId,
        string volumeSerialNumber,
        string rootFileId,
        string displayIdentityHash)
    {
        _ = workspaceId.Value;
        WorkspaceId = workspaceId;
        VolumeSerialNumber = RequireBounded(
            volumeSerialNumber,
            64,
            nameof(volumeSerialNumber));
        RootFileId = RequireBounded(rootFileId, 128, nameof(rootFileId));
        DisplayIdentityHash = RequireSha256(displayIdentityHash);
    }

    public WorkspaceId WorkspaceId { get; }

    public string VolumeSerialNumber { get; }

    public string RootFileId { get; }

    public string DisplayIdentityHash { get; }

    private static string RequireBounded(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value must contain between 1 and {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    private static string RequireSha256(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Display identity hash must contain exactly 64 lowercase hexadecimal characters.",
                nameof(value));
        }

        return value;
    }
}
