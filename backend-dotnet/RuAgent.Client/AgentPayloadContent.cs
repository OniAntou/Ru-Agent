namespace RuAgent.Client;

/// <summary>Client-visible payload content with its Host-declared type and encoding.</summary>
public sealed record AgentPayloadContent(string Kind, string Encoding, string Content);
