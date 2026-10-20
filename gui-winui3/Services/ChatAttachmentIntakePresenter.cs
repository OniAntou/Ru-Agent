namespace RuApp.Services;

internal sealed record ChatAttachmentIntakeResult(
    bool Accepted,
    bool IsImage,
    int MaximumBytes,
    string? Reason,
    string? Status);

/// <summary>Pure admission rules shared by picker, paste and drop intake.</summary>
internal static class ChatAttachmentIntakePresenter
{
    internal static string? TurnAdmissionStatus(int imageCount) =>
        imageCount > ChatAttachmentPolicy.MaxImageCount ? ChatAttachmentPolicy.TooManyImagesStatus : null;

    internal static ChatAttachmentIntakeResult Evaluate(string fileName, long size, int imageCount)
    {
        bool image = ChatAttachmentPolicy.IsImageFile(fileName);
        bool text = ChatAttachmentPolicy.IsTextOrCodeFile(fileName);
        if (!image && !text)
            return new(false, false, 0, "unsupported", $"File '{fileName}' is not supported. Only images and text/code files are accepted.");

        int maximum = image ? ChatAttachmentPolicy.MaxImageAttachmentBytes : ChatAttachmentPolicy.MaxTextAttachmentBytes;
        if (size > maximum)
        {
            int megabytes = maximum / (1024 * 1024);
            return new(false, image, maximum, "too_large", $"File '{fileName}' exceeds the {megabytes} MB attachment limit.");
        }

        if (imageCount >= ChatAttachmentPolicy.MaxImageCount && image)
            return new(false, true, maximum, "image_limit", ChatAttachmentPolicy.TooManyImagesStatus);

        return new(true, image, maximum, null, null);
    }

    internal static bool CanAddImage(int imageCount) => imageCount < ChatAttachmentPolicy.MaxImageCount;

    internal static bool OwnsSession(string sessionId, long generation, string activeSessionId, long activeGeneration) =>
        generation == activeGeneration && string.Equals(sessionId, activeSessionId, StringComparison.Ordinal);
}
