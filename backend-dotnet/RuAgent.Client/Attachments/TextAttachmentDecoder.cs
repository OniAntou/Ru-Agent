using System.Text;

namespace RuAgent.Client.Attachments;

/// <summary>
/// Decodes an attached text/code file for the prompt, refusing what is not text.
/// </summary>
/// <remarks>
/// Both clients used a lenient UTF-8 reader, so a binary file renamed to <c>.txt</c>, or
/// a file in a legacy code page, reached the model as a page of U+FFFD. A byte-order mark
/// selects UTF-8 or UTF-16; otherwise the bytes must be valid UTF-8, and a NUL byte (no
/// text file has one) marks the file as binary.
/// </remarks>
public static class TextAttachmentDecoder
{
    /// <summary>Why a file was refused, as a stable code the clients phrase.</summary>
    public const string NotTextCode = "attachment_not_text";

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text, out string? errorCode)
    {
        text = string.Empty;
        errorCode = null;
        Encoding encoding;
        var start = 0;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            encoding = new UTF8Encoding(false, true);
            start = 3;
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            start = 2;
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            start = 2;
        }
        else
        {
            if (bytes.Contains((byte)0))
            {
                errorCode = NotTextCode;
                return false;
            }

            encoding = new UTF8Encoding(false, true);
        }

        try
        {
            text = encoding.GetString(bytes[start..]);
        }
        catch (DecoderFallbackException)
        {
            errorCode = NotTextCode;
            return false;
        }

        if (text.Contains('\0'))
        {
            text = string.Empty;
            errorCode = NotTextCode;
            return false;
        }

        return true;
    }
}
