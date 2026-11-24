namespace RuAgent.Protocol.Harness;

/// <summary>
/// The create-run API's input bounds, shared by the Host and every client so a
/// composed prompt or an attached image is checked against the same number the
/// Host enforces.
/// </summary>
public static class AgentRunLimits
{
    /// <summary>
    /// Characters one run prompt may carry, typed text and inlined attachments
    /// together. 200,000 characters stay under the 1 MiB encrypted
    /// <c>UserPrompt</c> payload even at three UTF-8 bytes per character, and are
    /// about 50,000 tokens; the context admission still applies to every request.
    /// The bound used to be 16,000, which refused any source file larger than about
    /// 15 KB that lived outside the workspace.
    /// </summary>
    public const int MaxPromptChars = 200_000;

    /// <summary>
    /// Images one message (a run's prompt or a steer) may carry, and one model request to a
    /// hosted provider. Raised from 4; the decoded total below keeps any number of them
    /// inside the transport bound and the request size providers accept.
    /// </summary>
    public const int MaxImages = 10;

    /// <summary>
    /// Images one model request carries when the model runs on this machine or the local
    /// network, where many vision servers take only a few images per request.
    /// </summary>
    public const int MaxImagesPerLocalRequest = 4;

    /// <summary>Decoded bytes of one image attachment.</summary>
    public const long MaxImageBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Decoded bytes of all of one message's images, and of one request's. 16 MiB is what four
    /// images at the per-image bound already reached; as base64 it stays under the 24 MiB
    /// request body the Host reads and well under providers' request size limits.
    /// </summary>
    public const long MaxTotalImageBytes = 16L * 1024 * 1024;

    /// <summary>Characters of one <c>data:image/…;base64,</c> URL.</summary>
    public const int MaxImageDataUrlChars = 5_600_000;
}
