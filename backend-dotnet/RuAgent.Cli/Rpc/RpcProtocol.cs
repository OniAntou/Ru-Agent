namespace RuAgent.Cli.Rpc;

/// <summary>Wire constants for the stdio JSON-RPC bridge (protocol version 1).</summary>
public static class RpcProtocol
{
    public const int Version = 1;
    public const int MaxFrameCharacters = 1024 * 1024;
    public const int MaxOutboundMessages = 4096;
    public const int MaxMethodLength = 64;

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int ApplicationError = -32000;
    public const int NotInitialized = -32001;
    public const int AlreadyInitialized = -32002;
}
