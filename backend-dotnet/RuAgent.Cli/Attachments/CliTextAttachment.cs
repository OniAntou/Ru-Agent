using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// One pending text/code attachment. The text is inlined into the prompt at send
/// time (the run wire has no text-attachment field); nothing is persisted.
/// </summary>
public sealed record CliTextAttachment(string FileName, string TextContent, long ByteSize)
{
    public string Extension => Path.GetExtension(FileName).TrimStart('.');

    /// <summary>One-line attachment label: name and source size.</summary>
    public string Label => $"{FileName}  {CliImageAttachment.FormatBytes(ByteSize)}";
}
