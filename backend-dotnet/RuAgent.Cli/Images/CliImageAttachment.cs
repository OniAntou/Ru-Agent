namespace RuAgent.Cli.Images;

/// <summary>
/// One pending image attachment for a chat turn or a one-shot run. The base64 payload is
/// what travels on the wire; <see cref="TempPath"/> tracks a clipboard screenshot so the
/// temp file can be removed once the attachment is sent or detached.
/// </summary>
public sealed record CliImageAttachment(
    string FileName,
    string MimeType,
    string Base64Data,
    int Width,
    int Height,
    long ByteSize,
    string? TempPath = null)
{
    public string ToDataUrl() => $"data:{MimeType};base64,{Base64Data}";

    /// <summary>One-line attachment label: name, dimensions and size.</summary>
    public string Label => $"{FileName}  {Width}x{Height}  {FormatBytes(ByteSize)}";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "MB",
        >= 1024 => (bytes / 1024d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "KB",
        _ => bytes + "B",
    };
}
