using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

/// <summary>Preserves an explicit attachment notice when the selected model accepts only text.</summary>
internal static class UserImageInputProjection
{
    internal static (string Prompt, IReadOnlyList<ModelImage> Images) Project(
        string prompt, IReadOnlyList<ModelImage> images, bool acceptsImageInput)
    {
        if (images.Count == 0 || acceptsImageInput) return (prompt, images);

        var notice = $"[Image attachments: {images.Count}. The image contents are unavailable to this text-only model. "
            + "Do not infer or invent their contents. Continue helping with the accompanying text. "
            + "If the request depends on viewing an image, briefly explain that you cannot view it and ask "
            + "the user for a description or transcription.]";
        return (string.IsNullOrWhiteSpace(prompt) ? notice : prompt + "\n\n" + notice, []);
    }
}
