namespace RuAgent.Harness.Persistence;

public sealed class HarnessStoreException : Exception
{
    public HarnessStoreException(string code)
        : base(code)
    {
        Code = ValidateCode(code);
    }

    internal HarnessStoreException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = ValidateCode(code);
    }

    public string Code { get; }

    private static string ValidateCode(string? code)
    {
        if (code is not { Length: >= 1 and <= 64 }
            || code[0] is not (>= 'a' and <= 'z')
            || code.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '.'))
        {
            throw new ArgumentException("Store code must be a bounded safe token.", nameof(code));
        }

        return code;
    }
}
