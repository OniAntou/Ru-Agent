using System.Text.Json;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

public sealed class BrokerSessionException : Exception
{
    public BrokerSessionException(string code, string? message = null)
        : base(message ?? code)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class BrokerSession
{
    private readonly object _gate = new();
    private string? _activeSessionId;

    private BrokerSession(string descriptorPath, BrokerSessionDescriptor descriptor)
    {
        DescriptorPath = descriptorPath;
        Descriptor = descriptor;
    }

    public string DescriptorPath { get; }
    public BrokerSessionDescriptor Descriptor { get; }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _activeSessionId is not null;
            }
        }
    }

    public bool Open(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeSessionId is null)
            {
                _activeSessionId = sessionId;
                return true;
            }

            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public bool Owns(string sessionId)
    {
        lock (_gate)
        {
            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public void Close(string sessionId)
    {
        lock (_gate)
        {
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                _activeSessionId = null;
            }
        }
    }

    public static BrokerSession Load(string descriptorPath, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
        {
            throw new BrokerSessionException("missing_descriptor");
        }

        var fullDescriptorPath = Path.GetFullPath(descriptorPath);
        if (!File.Exists(fullDescriptorPath))
        {
            throw new BrokerSessionException("missing_descriptor");
        }

        BrokerSessionDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<BrokerSessionDescriptor>(
                File.ReadAllText(fullDescriptorPath),
                BrokerSessionDescriptor.JsonOptions);
        }
        catch (JsonException error)
        {
            throw new BrokerSessionException("invalid_descriptor", error.Message);
        }
        catch (IOException error)
        {
            throw new BrokerSessionException("invalid_descriptor", error.Message);
        }

        if (descriptor is null)
        {
            throw new BrokerSessionException("invalid_descriptor");
        }

        if (string.IsNullOrWhiteSpace(descriptor.AuthToken))
        {
            throw new BrokerSessionException("missing_token");
        }

        if (descriptor.ProtocolVersion != AgentEnvelopeCodec.ProtocolVersion)
        {
            throw new BrokerSessionException("unsupported_version");
        }

        if (!Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !baseUri.IsLoopback
            || baseUri.Port is < 1024 or > 65535)
        {
            throw new BrokerSessionException("invalid_base_url");
        }

        if (string.IsNullOrWhiteSpace(descriptor.SessionRoot)
            || !Path.IsPathRooted(descriptor.SessionRoot))
        {
            throw new BrokerSessionException("root_escape");
        }

        var sessionRoot = Path.GetFullPath(descriptor.SessionRoot);
        var descriptorDirectory = Path.GetDirectoryName(fullDescriptorPath)
            ?? throw new BrokerSessionException("invalid_descriptor");
        if (!IsUnderRoot(descriptorDirectory, sessionRoot))
        {
            throw new BrokerSessionException("root_escape");
        }

        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        if (descriptor.ExpiresAt <= effectiveNow)
        {
            throw new BrokerSessionException("expired");
        }

        var normalizedDescriptor = descriptor with { SessionRoot = sessionRoot };
        return new BrokerSession(fullDescriptorPath, normalizedDescriptor);
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
