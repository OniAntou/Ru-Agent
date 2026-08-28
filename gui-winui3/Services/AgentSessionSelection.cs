namespace RuApp.Services;

internal static class AgentSessionSelection
{
    private const int SessionIdLength = 32;

    public static bool IsValid(string? sessionId)
    {
        if (sessionId is null || sessionId.Length != SessionIdLength)
        {
            return false;
        }

        foreach (char character in sessionId)
        {
            bool lowerHex = character is >= '0' and <= '9'
                or >= 'a' and <= 'f';
            if (!lowerHex)
            {
                return false;
            }
        }

        return true;
    }
}
