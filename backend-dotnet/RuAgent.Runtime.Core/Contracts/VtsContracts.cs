namespace RuAgent.Runtime.Core.Contracts;

public sealed record VtsCatalog
{
    public VtsCatalog(
        string modelId,
        IEnumerable<string> hotkeyCodes,
        IEnumerable<string> expressionCodes)
        : this(modelId, string.Empty, hotkeyCodes, expressionCodes)
    {
    }

    public VtsCatalog(
        string modelId,
        string modelName,
        IEnumerable<string> hotkeyCodes,
        IEnumerable<string> expressionCodes)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 256)
        {
            throw new ArgumentException("VTube Studio model id is invalid.", nameof(modelId));
        }

        if (modelName is null || modelName.Length > 256)
        {
            throw new ArgumentException("VTube Studio model name is invalid.", nameof(modelName));
        }

        HotkeyCodes = NormalizeCodes(hotkeyCodes, nameof(hotkeyCodes));
        ExpressionCodes = NormalizeCodes(expressionCodes, nameof(expressionCodes));
        if (HotkeyCodes.Count + ExpressionCodes.Count > RuntimeLimits.MaxVtsCodes)
        {
            throw new ArgumentException("VTube Studio catalog is too large.", nameof(hotkeyCodes));
        }

        if (HotkeyCodes.Overlaps(ExpressionCodes))
        {
            throw new ArgumentException("VTube Studio catalog contains duplicate codes.", nameof(expressionCodes));
        }

        ModelId = modelId;
        ModelName = modelName;
    }

    public string ModelId { get; }
    public string ModelName { get; }
    public IReadOnlySet<string> HotkeyCodes { get; }
    public IReadOnlySet<string> ExpressionCodes { get; }
    public IReadOnlySet<string> AllCodes => HotkeyCodes.Concat(ExpressionCodes).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<string> ValidateActionCodes(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count is < 1 or > RuntimeLimits.MaxVtsActionCodes)
        {
            throw new ArgumentException("VTube Studio action count is invalid.", nameof(codes));
        }

        var normalized = codes.Select(NormalizeCode).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length
            || normalized.Any(code => !AllCodes.Contains(code)))
        {
            throw new ArgumentException("VTube Studio action code is not in the current catalog.", nameof(codes));
        }

        return normalized;
    }

    private static HashSet<string> NormalizeCodes(IEnumerable<string> codes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            var value = NormalizeCode(code);
            if (!normalized.Add(value))
            {
                throw new ArgumentException("VTube Studio catalog contains duplicate codes.", parameterName);
            }
        }

        return normalized;
    }

    private static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("VTube Studio code is invalid.", nameof(code));
        }

        if (code.Any(char.IsControl))
        {
            throw new ArgumentException("VTube Studio code is invalid.", nameof(code));
        }

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length > RuntimeLimits.MaxVtsCodeChars)
        {
            throw new ArgumentException("VTube Studio code is too long.", nameof(code));
        }

        return normalized;
    }
}
