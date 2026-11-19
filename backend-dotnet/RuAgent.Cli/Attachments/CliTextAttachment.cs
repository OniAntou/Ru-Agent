using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// One pending file attachment. Small text is inlined into the prompt at send time (the
/// run wire has no text-attachment field). Any other file is sent as a workspace
/// <see cref="ReferencePath"/> the model opens with <c>read</c>, exactly as the desktop app
/// does; <see cref="IsStaged"/> marks a copy made in <c>.ru/attachments</c> for a file that
/// lived outside the workspace.
/// </summary>
public sealed record CliTextAttachment(
    string FileName,
    string TextContent,
    long ByteSize,
    string? ReferencePath = null,
    bool IsStaged = false)
{
    public string Extension => Path.GetExtension(FileName).TrimStart('.');

    /// <summary>Whether the prompt carries only the workspace path, not the text.</summary>
    public bool IsReference => ReferencePath is not null;

    /// <summary>One-line attachment label: name (or workspace path) and source size.</summary>
    public string Label => IsStaged
        ? $"{FileName}  {CliImageAttachment.FormatBytes(ByteSize)}  (copied to {ReferencePath})"
        : IsReference
            ? $"{ReferencePath}  {CliImageAttachment.FormatBytes(ByteSize)}  (workspace reference)"
            : $"{FileName}  {CliImageAttachment.FormatBytes(ByteSize)}";
}
