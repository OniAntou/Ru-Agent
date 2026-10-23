namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelImage(string MimeType, string Base64Data)
{
    /// <summary>Base64 length of a 4 MiB image, with headroom.</summary>
    public const int MaximumBase64Chars = 5_600_000;

    private readonly bool _validated = Validate(MimeType, Base64Data);

    public string DataUrl => $"data:{MimeType};base64,{Base64Data}";

    private static bool Validate(string mimeType, string base64Data)
    {
        if (mimeType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new ArgumentException("MimeType is invalid. Only image/png, image/jpeg, image/webp are allowed.", "MimeType");
        }

        if (string.IsNullOrWhiteSpace(base64Data) || base64Data.Length > MaximumBase64Chars)
        {
            throw new ArgumentException("Base64Data is invalid.", "Base64Data");
        }

        return true;
    }

    /// <summary>
    /// A model name is only a hint. The authoritative answer is what the provider
    /// declares for the model, so a declared value always wins; the name check is the
    /// fallback for catalogs that publish no capability metadata at all.
    /// </summary>
    public static bool IsVisionModel(string? model, bool? declaredVision = null)
    {
        if (declaredVision.HasValue)
        {
            return declaredVision.Value;
        }

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
