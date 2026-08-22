using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools;

public sealed class ToolSchemaException : Exception
{
    public ToolSchemaException(string code) : base(code) => Code = code;

    public string Code { get; }
}

public static class ToolSchemaValidator
{
    private static readonly IReadOnlySet<string> SupportedKeywords = new HashSet<string>(
        ["required", "additionalProperties", "properties", "type", "enum", "const", "minimum", "maximum"],
        StringComparer.Ordinal);

    public static void Validate(JsonObject schema, JsonNode? value, string invalidCode = "invalid_arguments")
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(invalidCode))
        {
            throw new ArgumentException("invalid schema error code", nameof(invalidCode));
        }

        ValidateSchema(schema, value, invalidCode);
    }

    private static void ValidateSchema(JsonObject schema, JsonNode? value, string invalidCode)
    {
        if (schema.Any(item => !SupportedKeywords.Contains(item.Key)))
        {
            throw new ToolSchemaException("unsupported_tool_schema");
        }

        if (schema["type"] is JsonNode typeNode)
        {
            if (typeNode is not JsonValue typeValue
                || !typeValue.TryGetValue<string>(out var type)
                || !MatchesType(value, type))
            {
                throw new ToolSchemaException(invalidCode);
            }
        }

        if (schema["enum"] is JsonNode enumNode)
        {
            if (enumNode is not JsonArray choices
                || choices.Count == 0
                || !choices.Any(choice => JsonNode.DeepEquals(choice, value)))
            {
                throw new ToolSchemaException(invalidCode);
            }
        }

        if (schema.TryGetPropertyValue("const", out var constant)
            && !JsonNode.DeepEquals(constant, value))
        {
            throw new ToolSchemaException(invalidCode);
        }

        ValidateNumericBounds(schema, value, invalidCode);

        if (value is not JsonObject objectValue)
        {
            return;
        }

        var properties = schema["properties"] switch
        {
            null => new JsonObject(),
            JsonObject propertyObject => propertyObject,
            _ => throw new ToolSchemaException("unsupported_tool_schema"),
        };

        if (schema["required"] is JsonNode requiredNode)
        {
            if (requiredNode is not JsonArray required)
            {
                throw new ToolSchemaException("unsupported_tool_schema");
            }

            foreach (var item in required)
            {
                if (item is not JsonValue requiredValue
                    || !requiredValue.TryGetValue<string>(out var property)
                    || string.IsNullOrEmpty(property))
                {
                    throw new ToolSchemaException("unsupported_tool_schema");
                }

                if (!objectValue.ContainsKey(property))
                {
                    throw new ToolSchemaException(invalidCode);
                }
            }
        }

        if (schema["additionalProperties"] is JsonNode additionalNode)
        {
            if (additionalNode is not JsonValue additionalValue
                || !additionalValue.TryGetValue<bool>(out var allowAdditional))
            {
                throw new ToolSchemaException("unsupported_tool_schema");
            }

            if (!allowAdditional && objectValue.Any(item => !properties.ContainsKey(item.Key)))
            {
                throw new ToolSchemaException(invalidCode);
            }
        }

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject propertySchema)
            {
                throw new ToolSchemaException("unsupported_tool_schema");
            }

            if (objectValue.TryGetPropertyValue(property.Key, out var propertyValue))
            {
                ValidateSchema(propertySchema, propertyValue, invalidCode);
            }
        }
    }

    private static bool MatchesType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue stringValue && stringValue.TryGetValue<string>(out _),
        "integer" => IsInteger(value),
        "number" => TryNumber(value, out _),
        "boolean" => value is JsonValue boolValue && boolValue.TryGetValue<bool>(out _),
        "null" => value is null,
        _ => throw new ToolSchemaException("unsupported_tool_schema"),
    };

    private static void ValidateNumericBounds(JsonObject schema, JsonNode? value, string invalidCode)
    {
        var hasMinimum = schema.TryGetPropertyValue("minimum", out var minimumNode);
        var hasMaximum = schema.TryGetPropertyValue("maximum", out var maximumNode);
        if (!hasMinimum && !hasMaximum)
        {
            return;
        }

        if (!TryNumber(value, out var number))
        {
            throw new ToolSchemaException(invalidCode);
        }

        if (hasMinimum)
        {
            if (!TryNumber(minimumNode, out var minimum))
            {
                throw new ToolSchemaException("unsupported_tool_schema");
            }

            if (number < minimum)
            {
                throw new ToolSchemaException(invalidCode);
            }
        }

        if (hasMaximum)
        {
            if (!TryNumber(maximumNode, out var maximum))
            {
                throw new ToolSchemaException("unsupported_tool_schema");
            }

            if (number > maximum)
            {
                throw new ToolSchemaException(invalidCode);
            }
        }
    }

    private static bool IsInteger(JsonNode? value) => value is JsonValue integer
        && (integer.TryGetValue<int>(out _)
            || integer.TryGetValue<long>(out _)
            || integer.TryGetValue<uint>(out _)
            || integer.TryGetValue<ulong>(out _));

    private static bool TryNumber(JsonNode? value, out decimal number)
    {
        number = 0;
        if (value is not JsonValue numeric)
        {
            return false;
        }

        if (numeric.TryGetValue<int>(out var intValue))
        {
            number = intValue;
            return true;
        }

        if (numeric.TryGetValue<long>(out var longValue))
        {
            number = longValue;
            return true;
        }

        if (numeric.TryGetValue<uint>(out var uintValue))
        {
            number = uintValue;
            return true;
        }

        if (numeric.TryGetValue<ulong>(out var ulongValue))
        {
            number = ulongValue;
            return true;
        }

        if (numeric.TryGetValue<decimal>(out number))
        {
            return true;
        }

        if (numeric.TryGetValue<double>(out var doubleValue)
            && !double.IsNaN(doubleValue)
            && !double.IsInfinity(doubleValue)
            && doubleValue <= (double)decimal.MaxValue
            && doubleValue >= (double)decimal.MinValue)
        {
            number = (decimal)doubleValue;
            return true;
        }

        return false;
    }
}
