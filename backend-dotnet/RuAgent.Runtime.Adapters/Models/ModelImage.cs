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
}
