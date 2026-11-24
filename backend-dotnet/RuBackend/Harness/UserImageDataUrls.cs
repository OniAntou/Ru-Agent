using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

/// <summary>
/// The image attachments a user message may carry, checked the same way for a new run and
/// for a steer: at most <see cref="AgentRunLimits.MaxImages"/> PNG, JPEG or WebP data URLs,
/// each at most 4 MiB decoded and 16 MiB together.
/// </summary>
internal static class UserImageDataUrls
{
    /// <summary>Null when every image is acceptable; otherwise the bounded error to answer.</summary>
    public static AgentServiceResult? Validate(IReadOnlyList<string>? images)
    {
        if (images is not { Count: > 0 }) return null;
        if (images.Count > AgentRunLimits.MaxImages)
        {
            return BoundedError(400, "image_limit_exceeded", $"Too many images. Maximum is {AgentRunLimits.MaxImages}.");
        }

        long totalDecodedBytes = 0;
        foreach (var url in images)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BoundedError(400, "invalid_image_mime", "Image attachment cannot be empty.");
            }

            if (url.Length > AgentRunLimits.MaxImageDataUrlChars)
            {
                return BoundedError(413, "image_too_large", "Image attachment exceeds character limit.");
            }

            if (!url.StartsWith("data:image/", StringComparison.Ordinal))
            {
                return BoundedError(400, "invalid_image_mime", "Image data URL must start with 'data:image/'.");
            }

            var semi = url.IndexOf(";base64,", StringComparison.Ordinal);
            if (semi < 0)
            {
                return BoundedError(400, "invalid_image_base64", "Image data URL must contain ';base64,'.");
            }

            var mime = url.Substring(5, semi - 5);
            if (mime is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                return BoundedError(400, "invalid_image_mime", "Unsupported image mime type. Only image/png, image/jpeg, and image/webp are allowed.");
            }

            var b64 = url.Substring(semi + 8);
            if (!BoundedBase64Validator.TryGetDecodedLength(
                    b64,
                    AgentRunLimits.MaxImageDataUrlChars,
                    int.MaxValue,
                    out var decodedBytes))
            {
                return BoundedError(400, "invalid_image_base64", "Image Base64 data is malformed.");
            }

            if (decodedBytes > AgentRunLimits.MaxImageBytes)
            {
                return BoundedError(413, "image_too_large", "Image decoded size exceeds 4MB.");
            }

            totalDecodedBytes += decodedBytes;
            if (totalDecodedBytes > AgentRunLimits.MaxTotalImageBytes)
            {
                return BoundedError(413, "image_too_large", "Total images decoded size exceeds 16MB.");
            }
        }

        return null;
    }
}
