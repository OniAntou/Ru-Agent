namespace RuAgent.Protocol;

/// <summary>
/// An image a tool returns beside its JSON output (ADR 0026). The bytes never
/// enter the JSON output, so the output, payload and history text bounds stay
/// as they are; the image has its own bounds here.
/// </summary>
public sealed record ToolOutputImage
{
    /// <summary>Largest decoded image, the same bound as a user-attached image.</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>Largest width or height; providers reject larger images on every later step.</summary>
    public const int MaximumDimension = 8000;

    /// <summary>Newest tool images attached to one model request; older ones are omitted by name.</summary>
    public const int MaximumInContext = 4;

    /// <summary>Text appended to a tool result whose image was left out of the request.</summary>
    public const string OmittedNote = "[image omitted from this request; read the file again to view it]";

    public ToolOutputImage(string mediaType, ReadOnlyMemory<byte> bytes)
    {
        if (!IsSupportedMediaType(mediaType))
        {
            throw new ArgumentException("Tool image media type is not supported.", nameof(mediaType));
        }

        if (bytes.Length is 0 or > MaximumBytes)
        {
            throw new ArgumentException("Tool image size is out of bounds.", nameof(bytes));
        }

        MediaType = mediaType;
        Bytes = bytes.ToArray();
    }

    public string MediaType { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public static bool IsSupportedMediaType(string? mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/webp";

    /// <summary>
    /// The media type the leading bytes declare, or null when they are not a supported
    /// image. Stored image payloads hold raw bytes only, so this is also how a stored
    /// image recovers its type.
    /// </summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Records compare memory by reference; two images are equal when their bytes are.
    public bool Equals(ToolOutputImage? other) =>
        other is not null
        && MediaType == other.MediaType
        && Bytes.Span.SequenceEqual(other.Bytes.Span);

    public override int GetHashCode() => HashCode.Combine(MediaType, Bytes.Length);
}
