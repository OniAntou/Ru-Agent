using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace RuAgent.Protocol.Harness;

public static class WorkerV2Contracts
{
    public const int ProtocolVersion = 2;
    public const int MaxRequestIdChars = 128;
    public const int MaxCorrelationIdChars = 128;
    public const int MaxPromptChars = 16_000;
    public const int MaxTextChars = 1_000_000;
    public const int MaxDeltaChars = 64 * 1024;
    public const int MaxToolIdChars = 128;
    public const int MaxToolVersionChars = 32;
    public const int MaxProviderCallIdChars = 256;
    public const int MaxToolArgumentsBytes = 64 * 1024;
    public const int MaxToolSchemaBytes = 32 * 1024;
    public const int MaxSettingsBytes = 128 * 1024;
    public const int MaxFrameBytes = 5 * 1024 * 1024;
    public const int PromptContextSchemaVersion = 1;
    public const int MaxPromptContextBytes = 512 * 1024;
    public const int MaxPromptInstructionBytes = 64 * 1024;
    public const int MaxCombinedPromptInstructionBytes = 256 * 1024;
    public const int MaxPromptMemoryBytes = 100 * 1024;
    public const int MaxPromptReferenceBytes = 32 * 1024;
    public const int MaxPromptCollectionItems = 16;
    public const int MaxPromptReceiptBlocks = 32;
    public const long MaxSequence = long.MaxValue;

    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
    public const string AgentStepStart = "agent.step.start";
    public const string AgentStepDelta = "agent.step.delta";
    public const string AgentStepDone = "agent.step.done";
    public const string AgentStepCancel = "agent.step.cancel";
    public const string Error = "error";

    public static readonly IReadOnlySet<string> MessageTypes = new HashSet<string>(
        [Hello, HelloOk, AgentStepStart, AgentStepDelta, AgentStepDone, AgentStepCancel, Error],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> OrderedResponseTypes = new HashSet<string>(
        [HelloOk, AgentStepDelta, AgentStepDone, Error],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> FinishReasons = new HashSet<string>(
        ["stop", "tool_calls", "length"],
        StringComparer.Ordinal);

    private static readonly Regex SemVer = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.Compiled);
    private static readonly Regex SafeIdentifier = new(@"^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex PromptSourceIdentifier = new(@"^[A-Za-z0-9_.:-]+$", RegexOptions.Compiled);
    private static readonly Regex ErrorCode = new(@"^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> EnvelopeFields = new HashSet<string>(["v", "id", "type", "seq", "correlation_id", "payload"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> EnvelopeRequired = new HashSet<string>(["v", "id", "type", "payload"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StartFields = new HashSet<string>(["prompt", "turns", "system_prompt_context", "settings", "provider_bootstrap_path", "images"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> TurnFields = new HashSet<string>(["prompt", "response"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SettingFields = new HashSet<string>(
        ["model_tools", "reasoning", "reasoning_effort", "reasoning_model", "kilo_reasoning_enabled", "api_variant", "max_tokens"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ModelToolFields = new HashSet<string>(["tool_id", "tool_version", "scopes", "availability", "model_enabled", "capability_class", "verification_required"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ModelToolOptional = new HashSet<string>(["hint", "input_schema"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptContextFields = new HashSet<string>(["schema_version", "agent_profile", "preset", "environment", "instruction_blocks", "capabilities", "runtime_context"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptEnvironmentFields = new HashSet<string>(["model", "platform", "locale", "timezone", "date", "workspace_label"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptInstructionFields = new HashSet<string>(["source_id", "display_name", "content", "content_sha256", "origin"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptInstructionRequiredFields = new HashSet<string>(["source_id", "display_name", "content", "content_sha256"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptInstructionOrigins = new HashSet<string>(["Workspace", "ExternalSkill"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptCapabilityFields = new HashSet<string>(["id", "version", "description", "scope", "input_schema_json", "input_schema_digest", "example_arguments_json", "availability"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptRuntimeFields = new HashSet<string>(["memory", "mood", "voice", "vts", "hardware", "references"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptReferenceFields = new HashSet<string>(["kind", "label", "content"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptReceiptFields = new HashSet<string>(["composer_version", "prompt_sha256", "block_digests"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PromptBlockDigestFields = new HashSet<string>(["block_id", "sha256"], StringComparer.Ordinal);

    public static void ValidateEnvelope(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var fields = envelope.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (!EnvelopeRequired.IsSubsetOf(fields) || !fields.IsSubsetOf(EnvelopeFields))
        {
            throw new AgentProtocolException("invalid_envelope");
        }

        var versionNode = envelope["v"];
        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version) || versionValue.TryGetValue<bool>(out _))
        {
            throw new AgentProtocolException("invalid_version");
        }

        if (version != ProtocolVersion)
        {
            throw new AgentProtocolException("unsupported_version");
        }

        var idNode = envelope["id"];
        if (!IsSafeIdentifier(idNode, MaxRequestIdChars))
        {
            throw new AgentProtocolException("invalid_id");
        }

        var typeNode = envelope["type"];
        if (typeNode is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || !MessageTypes.Contains(type))
        {
            throw new AgentProtocolException("unsupported_message");
        }

        var hasSeq = envelope.ContainsKey("seq");
        var seqNode = envelope["seq"];
        long seq = 0;
        bool seqValid = seqNode is null || (seqNode is JsonValue sv && TryGetLong(sv, out seq) && seq >= 0 && seq <= MaxSequence);
        if (!seqValid)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        if (OrderedResponseTypes.Contains(type) && !hasSeq)
        {
            throw new AgentProtocolException("missing_sequence");
        }

        if (!OrderedResponseTypes.Contains(type) && hasSeq)
        {
            throw new AgentProtocolException("invalid_sequence");
        }

        if (hasSeq)
        {
            if (type == HelloOk && seq != 0)
            {
                throw new AgentProtocolException("invalid_sequence");
            }

            if (OrderedResponseTypes.Contains(type) && type != HelloOk && seq < 1)
            {
                throw new AgentProtocolException("invalid_sequence");
            }
        }

        var correlationNode = envelope["correlation_id"];
        if (correlationNode is not null && !IsSafeIdentifier(correlationNode, MaxCorrelationIdChars))
        {
            throw new AgentProtocolException("invalid_correlation_id");
        }

        var payloadNode = envelope["payload"];
        if (payloadNode is not JsonObject payload)
        {
            throw new AgentProtocolException("invalid_payload");
        }

        switch (type)
        {
            case Hello:
                ValidateHello(payload);
                break;
            case HelloOk:
                ValidateHelloOk(payload);
                break;
            case AgentStepStart:
                ValidateStart(payload);
                break;
            case AgentStepDelta:
                ValidateDelta(payload);
                break;
            case AgentStepDone:
                ValidateStepDone(payload);
                break;
            case AgentStepCancel:
                if (payload.Count != 0)
                {
                    throw new AgentProtocolException("invalid_cancel");
                }
                break;
            case Error:
                ValidateError(payload);
                break;
        }

        var canonical = CanonicalJson(envelope);
        if (Encoding.UTF8.GetByteCount(canonical) > MaxFrameBytes)
        {
            throw new AgentProtocolException("message_too_large");
        }
    }

    public static void ValidateHello(JsonObject payload)
    {
        if (payload.Count != 1 || !payload.ContainsKey("token"))
        {
            throw new AgentProtocolException("invalid_hello");
        }

        var tokenNode = payload["token"];
        if (tokenNode is not JsonValue tv || !tv.TryGetValue<string>(out var token) || string.IsNullOrWhiteSpace(token) || token != token.Trim() || !IsAscii(token) || token.Length > 4096)
        {
            throw new AgentProtocolException("invalid_hello");
        }
    }

    public static void ValidateHelloOk(JsonObject payload)
    {
        if (payload.Count != 2 || !payload.ContainsKey("version") || !payload.ContainsKey("capabilities"))
        {
            throw new AgentProtocolException("invalid_hello");
        }

        var versionNode = payload["version"];
        if (versionNode is not JsonValue vv || !vv.TryGetValue<int>(out var version) || version != ProtocolVersion)
        {
            throw new AgentProtocolException("invalid_hello");
        }

        var capsNode = payload["capabilities"];
        if (capsNode is not JsonArray caps)
        {
            throw new AgentProtocolException("invalid_hello");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in caps)
        {
            if (!IsSafeIdentifier(item, 128) || !seen.Add(((JsonValue)item!).GetValue<string>()))
            {
                throw new AgentProtocolException("invalid_hello");
            }
        }
    }

    public static void ValidateStart(JsonObject payload)
    {
        if (!payload.ContainsKey("prompt") || !payload.Select(p => p.Key).All(k => StartFields.Contains(k)))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var hasImages = false;
        if (payload.ContainsKey("images"))
        {
            if (payload["images"] is not JsonArray imagesArray)
            {
                throw new AgentProtocolException("invalid_request");
            }
            if (imagesArray.Count > 4)
            {
                throw new AgentProtocolException("invalid_request");
            }
            foreach (var imgNode in imagesArray)
            {
                if (imgNode is not JsonValue iv || !iv.TryGetValue<string>(out var imgStr) || string.IsNullOrWhiteSpace(imgStr))
                {
                    throw new AgentProtocolException("invalid_request");
                }
                if (!imgStr.StartsWith("data:image/", StringComparison.Ordinal) || !imgStr.Contains(";base64,", StringComparison.Ordinal))
                {
                    throw new AgentProtocolException("invalid_request");
                }
            }
            hasImages = imagesArray.Count > 0;
        }

        var promptNode = payload["prompt"];
        if (promptNode is not JsonValue pv || !pv.TryGetValue<string>(out var prompt) || (prompt.Length == 0 ? !hasImages : (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptChars)))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var turnsNode = payload["turns"];
        JsonArray turns = turnsNode as JsonArray ?? new JsonArray();
        if (turnsNode is not null && turnsNode is not JsonArray)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (turns.Count > 8)
        {
            throw new AgentProtocolException("invalid_request");
        }

        foreach (var turnNode in turns)
        {
            if (turnNode is not JsonObject turn || turn.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(TurnFields) == false)
            {
                throw new AgentProtocolException("invalid_request");
            }

            var tp = turn["prompt"];
            var tr = turn["response"];
            if (tp is not JsonValue tpv || !tpv.TryGetValue<string>(out var promptVal) || string.IsNullOrWhiteSpace(promptVal) || promptVal.Length > MaxPromptChars)
            {
                throw new AgentProtocolException("invalid_request");
            }

            if (tr is not JsonValue trv || !trv.TryGetValue<string>(out var responseVal) || string.IsNullOrWhiteSpace(responseVal) || responseVal.Length > 32_000)
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        var promptContextNode = payload["system_prompt_context"];
        if (promptContextNode is not JsonObject promptContext)
        {
            throw new AgentProtocolException("invalid_request");
        }

        ValidatePromptContext(promptContext);

        if (payload.ContainsKey("provider_bootstrap_path")
            && (payload["provider_bootstrap_path"] is not JsonValue bootstrapPathValue
                || !bootstrapPathValue.TryGetValue<string>(out var bootstrapPath)
                || string.IsNullOrWhiteSpace(bootstrapPath)
                || bootstrapPath.Length > 4096
                || bootstrapPath.Contains('\0')
                || bootstrapPath.Any(char.IsControl)))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var settingsNode = payload["settings"];
        JsonObject settings = settingsNode as JsonObject ?? new JsonObject();
        if (settingsNode is not null && settingsNode is not JsonObject)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (settingsNode is not null && settings.Select(p => p.Key).Any(k => !SettingFields.Contains(k)))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var toolsNode = settings["model_tools"];
        JsonArray tools = toolsNode as JsonArray ?? new JsonArray();
        if (toolsNode is not null && toolsNode is not JsonArray)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (tools.Count > 16)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolNode in tools)
        {
            ValidateModelTool(toolNode, toolIds);
        }

        var settingsJson = JsonSerializer.Serialize(settings);
        if (Encoding.UTF8.GetByteCount(settingsJson) > MaxSettingsBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }
    }

    private static void ValidatePromptContext(JsonObject context)
    {
        if (!HasExactFields(context, PromptContextFields))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (!TryGetInt(context["schema_version"], out var schemaVersion)
            || schemaVersion != PromptContextSchemaVersion)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (!TryGetString(context["agent_profile"], 64, out var profile)
            || !string.Equals(profile, "ru-agent", StringComparison.Ordinal))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (!TryGetString(context["preset"], 32, out var preset)
            || (preset != "standard" && preset != "full_access"))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (context["environment"] is not JsonObject environment
            || !HasExactFields(environment, PromptEnvironmentFields)
            || !TryGetString(environment["model"], 256, out _)
            || !TryGetString(environment["platform"], 64, out _)
            || !TryGetString(environment["locale"], 64, out _)
            || !TryGetString(environment["timezone"], 128, out _)
            || !TryGetString(environment["date"], 32, out _)
            || !TryGetNullableString(environment["workspace_label"], 128, out _))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var instructionNode = context["instruction_blocks"];
        if (instructionNode is not JsonArray instructions || instructions.Count > MaxPromptCollectionItems)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var instructionIds = new HashSet<string>(StringComparer.Ordinal);
        var combinedInstructionBytes = 0;
        foreach (var instructionNodeItem in instructions)
        {
            if (instructionNodeItem is not JsonObject instruction
                || !HasRequiredFields(instruction, PromptInstructionRequiredFields, PromptInstructionFields)
                || !TryGetPromptIdentifier(instruction["source_id"], 128, allowColon: true, out var sourceId)
                || !TryGetString(instruction["display_name"], 256, out _)
                || !TryGetString(instruction["content"], MaxPromptInstructionBytes, out var content)
                || !TryGetDigest(instruction["content_sha256"], out var contentDigest)
                || (instruction.ContainsKey("origin")
                    && (!TryGetString(instruction["origin"], 32, out var origin)
                        || !PromptInstructionOrigins.Contains(origin)))
                || !instructionIds.Add(sourceId))
            {
                throw new AgentProtocolException("invalid_request");
            }

            var normalizedContent = NormalizePromptText(content);
            var contentBytes = PromptUtf8ByteCount(normalizedContent);
            if (contentBytes == 0 || contentBytes > MaxPromptInstructionBytes)
            {
                throw new AgentProtocolException("invalid_request");
            }

            combinedInstructionBytes = checked(combinedInstructionBytes + contentBytes);
            if (!string.Equals(contentDigest, ComputeSha256(normalizedContent), StringComparison.Ordinal))
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        if (combinedInstructionBytes > MaxCombinedPromptInstructionBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var capabilityNode = context["capabilities"];
        if (capabilityNode is not JsonArray capabilities || capabilities.Count > MaxPromptCollectionItems)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capabilityNodeItem in capabilities)
        {
            if (capabilityNodeItem is not JsonObject capability
                || !HasExactFields(capability, PromptCapabilityFields)
                || !TryGetPromptIdentifier(capability["id"], 128, allowColon: false, out var capabilityId)
                || !TryGetSemVer(capability["version"], out _)
                || !TryGetString(capability["description"], 2 * 1024, out _)
                || !TryGetPromptIdentifier(capability["scope"], 128, allowColon: false, out _)
                || !TryGetCanonicalObject(capability["input_schema_json"], MaxToolSchemaBytes, out var schemaJson)
                || !TryGetDigest(capability["input_schema_digest"], out var schemaDigest)
                || !string.Equals(schemaDigest, ComputeSha256(schemaJson), StringComparison.Ordinal)
                || !TryGetOptionalCanonicalObject(capability["example_arguments_json"], 4 * 1024)
                || !TryGetPromptIdentifier(capability["availability"], 32, allowColon: false, out var availability)
                || !capabilityIds.Add(capabilityId))
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        if (context["runtime_context"] is not JsonObject runtime
            || !HasExactFields(runtime, PromptRuntimeFields)
            || !TryGetNullableString(runtime["memory"], MaxPromptMemoryBytes, out var memory)
            || !TryGetNullableString(runtime["mood"], 256, out _)
            || !TryGetNullableString(runtime["voice"], 2 * 1024, out _)
            || !TryGetNullableString(runtime["vts"], 2 * 1024, out _)
            || !TryGetNullableString(runtime["hardware"], 2 * 1024, out _))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (memory is not null && PromptUtf8ByteCount(memory) > MaxPromptMemoryBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (runtime["references"] is not JsonArray references || references.Count > MaxPromptCollectionItems)
        {
            throw new AgentProtocolException("invalid_request");
        }

        foreach (var referenceNode in references)
        {
            if (referenceNode is not JsonObject reference
                || !HasExactFields(reference, PromptReferenceFields)
                || !TryGetPromptIdentifier(reference["kind"], 64, allowColon: false, out _)
                || !TryGetString(reference["label"], 256, out _)
                || !TryGetString(reference["content"], MaxPromptReferenceBytes, out var referenceContent)
                || PromptUtf8ByteCount(NormalizePromptText(referenceContent)) > MaxPromptReferenceBytes)
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        if (PromptUtf8ByteCount(CanonicalJson(context)) > MaxPromptContextBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }

    }

    private static void ValidatePromptReceipt(JsonObject receipt)
    {
        if (!HasExactFields(receipt, PromptReceiptFields)
            || !TryGetString(receipt["composer_version"], 32, out var composerVersion)
            || composerVersion != "1.0.0"
            || !TryGetDigest(receipt["prompt_sha256"], out _)
            || receipt["block_digests"] is not JsonArray blocks
            || blocks.Count > MaxPromptReceiptBlocks)
        {
            throw new AgentProtocolException("invalid_step");
        }

        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blockNode in blocks)
        {
            if (blockNode is not JsonObject block
                || !HasExactFields(block, PromptBlockDigestFields)
                || !TryGetPromptIdentifier(block["block_id"], 256, allowColon: true, out var blockId)
                || !TryGetDigest(block["sha256"], out _)
                || !blockIds.Add(blockId))
            {
                throw new AgentProtocolException("invalid_step");
            }
        }
    }

    private static bool HasExactFields(JsonObject value, IReadOnlySet<string> expected)
    {
        return value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool HasRequiredFields(JsonObject value, IReadOnlySet<string> required, IReadOnlySet<string> allowed)
    {
        var fields = value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        return required.IsSubsetOf(fields) && fields.IsSubsetOf(allowed);
    }

    private static bool TryGetString(JsonNode? node, int maxChars, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maxChars
            || text.Contains('\0'))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(text);
            value = text;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetNullableString(JsonNode? node, int maxCharsOrBytes, out string? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text.Contains('\0')
            || text.Length > maxCharsOrBytes)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(text);
            value = text;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetPromptIdentifier(JsonNode? node, int maxChars, bool allowColon, out string value)
    {
        value = string.Empty;
        if (!TryGetString(node, maxChars, out var text)
            || (allowColon ? !PromptSourceIdentifier.IsMatch(text) : !SafeIdentifier.IsMatch(text)))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetDigest(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text.Length != 64
            || text.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetCanonicalObject(JsonNode? node, int maxBytes, out string canonical)
    {
        canonical = string.Empty;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var json)
            || string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject objectNode)
            {
                return false;
            }

            canonical = objectNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            return string.Equals(canonical, json, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetOptionalCanonicalObject(JsonNode? node, int maxBytes)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return true;
        }

        return TryGetCanonicalObject(node, maxBytes, out _);
    }

    private static bool TryGetSemVer(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (!TryGetString(node, MaxToolVersionChars, out var text) || !SemVer.IsMatch(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out value)
            && !jsonValue.TryGetValue<bool>(out _);
    }

    private static string NormalizePromptText(string value)
    {
        return value.Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();
    }

    private static string ComputeSha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))).ToLowerInvariant();
    }

    private static int PromptUtf8ByteCount(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new AgentProtocolException("invalid_request");
        }
    }

    public static void ValidateDelta(JsonObject payload)
    {
        if (payload.Count != 1 || !payload.ContainsKey("text"))
        {
            throw new AgentProtocolException("invalid_delta");
        }

        var textNode = payload["text"];
        if (textNode is not JsonValue tv || !tv.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text) || text.Length > MaxDeltaChars)
        {
            throw new AgentProtocolException("invalid_delta");
        }
    }

    public static void ValidateStepDone(JsonObject payload)
    {
        if (payload.Count != 4 || !payload.ContainsKey("text") || !payload.ContainsKey("tool_calls") || !payload.ContainsKey("finish_reason") || !payload.ContainsKey("prompt_receipt"))
        {
            throw new AgentProtocolException("invalid_step");
        }

        if (payload["prompt_receipt"] is not JsonObject promptReceipt)
        {
            throw new AgentProtocolException("invalid_step");
        }

        ValidatePromptReceipt(promptReceipt);

        var textNode = payload["text"];
        if (textNode is not null && textNode.GetValueKind() != JsonValueKind.Null)
        {
            if (textNode is not JsonValue tv || !tv.TryGetValue<string>(out var text) || text.Length > MaxTextChars)
            {
                throw new AgentProtocolException("invalid_step");
            }
        }

        var callsNode = payload["tool_calls"];
        if (callsNode is not JsonArray calls || calls.Count > 1)
        {
            throw new AgentProtocolException("invalid_step");
        }

        foreach (var callNode in calls)
        {
            if (callNode is not JsonObject call || call.Count != 4 || !call.ContainsKey("provider_call_id") || !call.ContainsKey("tool_id") || !call.ContainsKey("tool_version") || !call.ContainsKey("arguments"))
            {
                throw new AgentProtocolException("invalid_step");
            }

            var providerNode = call["provider_call_id"];
            if (providerNode is not null && providerNode.GetValueKind() != JsonValueKind.Null)
            {
                if (providerNode is not JsonValue pv || !pv.TryGetValue<string>(out var provider) || string.IsNullOrWhiteSpace(provider) || provider.Length > MaxProviderCallIdChars)
                {
                    throw new AgentProtocolException("invalid_step");
                }
            }

            var toolIdNode = call["tool_id"];
            if (toolIdNode is not JsonValue tidv || !tidv.TryGetValue<string>(out var toolId) || string.IsNullOrWhiteSpace(toolId) || toolId.Length > MaxToolIdChars)
            {
                throw new AgentProtocolException("invalid_step");
            }

            var versionNode = call["tool_version"];
            if (versionNode is not JsonValue vv || !vv.TryGetValue<string>(out var version) || version.Length > MaxToolVersionChars || !SemVer.IsMatch(version))
            {
                throw new AgentProtocolException("invalid_step");
            }

            var argsNode = call["arguments"];
            if (argsNode is not JsonObject)
            {
                throw new AgentProtocolException("invalid_step");
            }

            var argsJson = JsonSerializer.Serialize(argsNode);
            if (Encoding.UTF8.GetByteCount(argsJson) > MaxToolArgumentsBytes)
            {
                throw new AgentProtocolException("invalid_step");
            }
        }

        var finishNode = payload["finish_reason"];
        if (finishNode is not JsonValue fv || !fv.TryGetValue<string>(out var finish) || !FinishReasons.Contains(finish))
        {
            throw new AgentProtocolException("invalid_step");
        }

        var callsCount = calls.Count;
        if (finish == "tool_calls" && callsCount != 1)
        {
            throw new AgentProtocolException("invalid_step");
        }

        if (callsCount > 0 && finish != "tool_calls")
        {
            throw new AgentProtocolException("invalid_step");
        }

        var hasText = textNode is not null && textNode.GetValueKind() != JsonValueKind.Null && textNode is JsonValue tvr && tvr.TryGetValue<string>(out var t) && !string.IsNullOrEmpty(t);
        // If tool_calls present, text must be null
        if (callsCount > 0 && textNode is not null && textNode.GetValueKind() != JsonValueKind.Null)
        {
            // need to check if text is not null value
            if (hasText)
            {
                throw new AgentProtocolException("invalid_step");
            }
            // also if text is null, it's allowed to be null but we already know calls present, so check case where text is JsonValue null
            // In our earlier check, hasText false means text is null or empty; but spec says tool call step cannot contain visible text (null allowed). However payload definition requires text field to be null when tool_calls present.
            // The fixture has "text": null when tool_calls present, so that's allowed. So only fail if text is non-null string.
            // Already handled: if callsCount>0 and textNode not null and textNode is string with non-null, we threw. If it's Json null, it's ok.
        }
    }

    public static void ValidateError(JsonObject payload)
    {
        if (payload.Count != 2 || !payload.ContainsKey("code") || !payload.ContainsKey("retryable"))
        {
            throw new AgentProtocolException("invalid_error");
        }

        var codeNode = payload["code"];
        if (codeNode is not JsonValue cv || !cv.TryGetValue<string>(out var code) || !ErrorCode.IsMatch(code))
        {
            throw new AgentProtocolException("invalid_error");
        }

        var retryNode = payload["retryable"];
        if (retryNode is not JsonValue rv || !rv.TryGetValue<bool>(out _))
        {
            throw new AgentProtocolException("invalid_error");
        }
    }

    public static string CanonicalJson(JsonObject value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
    }

    private static bool IsSafeIdentifier(JsonNode? node, int max)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > max)
        {
            return false;
        }

        return SafeIdentifier.IsMatch(text);
    }

    private static bool IsAscii(string value) => value.All(c => c < 128);

    private static bool TryGetLong(JsonValue value, out long result)
    {
        if (value.TryGetValue<long>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var i))
        {
            result = i;
            return true;
        }

        result = 0;
        return false;
    }

    private static void ValidateOptionalInt(JsonObject settings, string field, int min, int max)
    {
        var node = settings[field];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out _))
            {
                throw new AgentProtocolException("invalid_request");
            }

            if (!value.TryGetValue<int>(out var intVal) && !value.TryGetValue<long>(out var longVal))
            {
                throw new AgentProtocolException("invalid_request");
            }

            long actual = node is JsonValue v && v.TryGetValue<int>(out var iv) ? iv : (value.TryGetValue<long>(out var lv) ? lv : 0);
            // Need to ensure we get correct value; simplify check via TryGet int.
            if (value.TryGetValue<int>(out var checkedInt))
            {
                if (checkedInt < min || checkedInt > max)
                {
                    throw new AgentProtocolException("invalid_request");
                }
            }
            else if (value.TryGetValue<long>(out var checkedLong))
            {
                if (checkedLong < min || checkedLong > max || checkedLong != (int)checkedLong)
                {
                    throw new AgentProtocolException("invalid_request");
                }
            }
            else if (value.TryGetValue<double>(out var d))
            {
                // integer field cannot be double
                throw new AgentProtocolException("invalid_request");
            }
        }
        else if (node is not null)
        {
            throw new AgentProtocolException("invalid_request");
        }
    }

    private static void ValidateOptionalNumber(JsonObject settings, string field, double min, double max)
    {
        var node = settings[field];
        if (node is not JsonValue value)
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (value.TryGetValue<bool>(out _))
        {
            throw new AgentProtocolException("invalid_request");
        }

        double actual;
        if (value.TryGetValue<int>(out var iv))
        {
            actual = iv;
        }
        else if (value.TryGetValue<long>(out var lv))
        {
            actual = lv;
        }
        else if (value.TryGetValue<double>(out var dv))
        {
            actual = dv;
        }
        else
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (!double.IsFinite(actual) || actual < min || actual > max)
        {
            throw new AgentProtocolException("invalid_request");
        }
    }

    private static void ValidateModelTool(JsonNode? node, HashSet<string> toolIds)
    {
        if (node is not JsonObject tool)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var fields = tool.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (!ModelToolFields.IsSubsetOf(fields) || !fields.IsSubsetOf(ModelToolFields.Union(ModelToolOptional)))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var toolIdNode = tool["tool_id"];
        var versionNode = tool["tool_version"];
        if (toolIdNode is not JsonValue tidv || !tidv.TryGetValue<string>(out var toolId) || string.IsNullOrWhiteSpace(toolId) || toolId.Length > MaxToolIdChars || toolIds.Contains(toolId))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (versionNode is not JsonValue vv || !vv.TryGetValue<string>(out var version) || version.Length > MaxToolVersionChars || !SemVer.IsMatch(version))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var scopesNode = tool["scopes"];
        if (scopesNode is not JsonArray scopes || scopes.Count > 16)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var scopeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in scopes)
        {
            if (!IsSafeIdentifier(s, 128) || !scopeSet.Add(((JsonValue)s!).GetValue<string>()))
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        var availabilityNode = tool["availability"];
        if (availabilityNode is not JsonValue av || !av.TryGetValue<string>(out var availability) || (availability != "executable" && availability != "native_pending"))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var modelEnabledNode = tool["model_enabled"];
        if (modelEnabledNode is not JsonValue mev || !mev.TryGetValue<bool>(out var modelEnabled) || modelEnabled != true)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var capClassNode = tool["capability_class"];
        if (capClassNode is not JsonValue ccv || !ccv.TryGetValue<string>(out var capClass) || (capClass != "tool" && capClass != "native_mutation"))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var verificationNode = tool["verification_required"];
        if (verificationNode is not JsonValue vrv || !vrv.TryGetValue<bool>(out var verificationRequired))
        {
            throw new AgentProtocolException("invalid_request");
        }

        if (verificationRequired != (capClass == "native_mutation"))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var hintNode = tool["hint"];
        if (hintNode is not null && (hintNode is not JsonValue hv || !hv.TryGetValue<string>(out var hint) || hint.Length > 1024))
        {
            throw new AgentProtocolException("invalid_request");
        }

        var schemaNode = tool["input_schema"];
        if (schemaNode is not null)
        {
            if (schemaNode is not JsonObject)
            {
                throw new AgentProtocolException("invalid_request");
            }

            var schemaJson = JsonSerializer.Serialize(schemaNode);
            if (Encoding.UTF8.GetByteCount(schemaJson) > MaxToolSchemaBytes)
            {
                throw new AgentProtocolException("invalid_request");
            }
        }

        toolIds.Add(toolId);
    }
}
