namespace RuAgent.Client;

/// <summary>Formats token counts consistently across Ru surfaces.</summary>
public static class TokenCountFormatter
{
    public static string Format(long tokens)
    {
        if (tokens < 1000)
        {
            return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tokens >= 1_000_000)
        {
            return (tokens / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }

        return (tokens / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
    }
}
