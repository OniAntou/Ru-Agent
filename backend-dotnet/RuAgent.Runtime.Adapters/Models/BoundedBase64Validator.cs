namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Validates standard Base64 while enforcing encoded and decoded size bounds.</summary>
public static class BoundedBase64Validator
{
    public static bool TryGetDecodedLength(
        string? value,
        int maximumEncodedCharacters,
        int maximumDecodedBytes,
        out int decodedLength)
    {
        decodedLength = 0;
        if (string.IsNullOrEmpty(value)
            || maximumEncodedCharacters < 1
            || maximumDecodedBytes < 1
            || value.Length > maximumEncodedCharacters
            || value.Length % 4 != 0)
        {
            return false;
        }

        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
            : value.EndsWith('=') ? 1
            : 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '=')
            {
                if (index < value.Length - padding || padding == 0)
                {
                    return false;
                }

                continue;
            }

            if (!((character is >= 'A' and <= 'Z')
                || (character is >= 'a' and <= 'z')
                || (character is >= '0' and <= '9')
                || character is '+' or '/'))
            {
                return false;
            }
        }

        if ((padding == 2 && (Base64Value(value[^3]) & 0b1111) != 0)
            || (padding == 1 && (Base64Value(value[^2]) & 0b11) != 0))
        {
            return false;
        }

        var expectedDecodedLength = (long)(value.Length / 4) * 3 - padding;
        if (expectedDecodedLength <= 0 || expectedDecodedLength > maximumDecodedBytes)
        {
            return false;
        }

        var buffer = new byte[(int)expectedDecodedLength];
        if (!Convert.TryFromBase64Chars(value.AsSpan(), buffer, out var written)
            || written != expectedDecodedLength)
        {
            return false;
        }

        decodedLength = written;
        return true;
    }

    private static int Base64Value(char value) => value switch
    {
        >= 'A' and <= 'Z' => value - 'A',
        >= 'a' and <= 'z' => value - 'a' + 26,
        >= '0' and <= '9' => value - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };
}
