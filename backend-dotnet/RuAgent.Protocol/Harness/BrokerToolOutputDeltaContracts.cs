namespace RuAgent.Protocol.Harness;

/// <summary>
/// Wire contract for the v2-only <c>tool.output.delta</c> message. Frames bind to
/// run/tool/execution identity, carry a monotonic per-execution frame sequence, name
/// their stream, and never masquerade as the final receipt — only <c>tool.result</c> is
/// durable and terminal.
/// </summary>
public sealed record BrokerToolOutputDeltaWire(
    string RunId,
    string ToolCallId,
    string ExecutionId,
    long FrameSequence,
    string Stream,
    string Text,
    bool Redacted)
{
    public const int MaxTextChars = 8_192;

    public static void Validate(BrokerToolOutputDeltaWire delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ValidateId(delta.RunId, "invalid_delta_run");
        ValidateId(delta.ToolCallId, "invalid_delta_tool_call");
        ValidateId(delta.ExecutionId, "invalid_delta_execution");
        if (delta.FrameSequence < 1)
        {
            throw new AgentProtocolException("invalid_delta_sequence");
        }

        if (delta.Stream is not ("stdout" or "stderr"))
        {
            throw new AgentProtocolException("invalid_delta_stream");
        }

        if (delta.Text.Length > MaxTextChars || delta.Text.Contains('\0'))
        {
            throw new AgentProtocolException("invalid_delta_text");
        }
    }

    private static void ValidateId(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > AgentEnvelopeCodec.MaxIdLength)
        {
            throw new AgentProtocolException(code);
        }
    }
}
