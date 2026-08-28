namespace RuApp.Services;

public sealed record AgentPayloadModel(
    string RunId,
    string PayloadId,
    string Content)
{
    public static AgentPayloadModel FromContent(string runId, string payloadId, string content) =>
        new(runId, payloadId, content);
}
