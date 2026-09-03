namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelImage(string MimeType, string Base64Data)
{
    private readonly bool _validated = Validate(MimeType, Base64Data);

    private static bool Validate(string mimeType, string base64Data)
    {
        if (mimeType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new ArgumentException("MimeType is invalid. Only image/png, image/jpeg, image/webp are allowed.", "MimeType");
        }

        if (string.IsNullOrWhiteSpace(base64Data) || base64Data.Length > 5_600_000)
        {
            throw new ArgumentException("Base64Data is invalid.", "Base64Data");
        }

        return true;
    }

    public static bool IsVisionModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim().ToLowerInvariant();
        return m.Contains("gpt-4o")
            || m.Contains("gpt-5")
            || m.Contains("claude-3")
            || m.Contains("claude-4")
            || m.Contains("claude-5")
            || m.Contains("claude-sonnet")
            || m.Contains("claude-opus")
            || m.Contains("gemini")
            || m.Contains("vision")
            || m.Contains("o1")
            || m.Contains("llama-3.2-vision");
    }
}
