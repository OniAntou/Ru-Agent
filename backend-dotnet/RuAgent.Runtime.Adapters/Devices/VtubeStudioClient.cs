using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;

namespace RuAgent.Runtime.Adapters.Devices;

public sealed class VtubeStudioClient : IVtubeStudioClient, IAsyncDisposable
{
    private const string ApiName = "VTubeStudioPublicAPI";
    private const string ApiVersion = "1.0";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly VtubeStudioClientOptions _options;
    private readonly IVtubeStudioTokenStore _tokenStore;
    private readonly IVtubeStudioWebSocketFactory _socketFactory;
    private readonly RuntimeLeaseCoordinator? _leases;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IVtubeStudioWebSocket? _socket;
    private int _disposed;

    public VtubeStudioClient(
        VtubeStudioClientOptions options,
        IVtubeStudioTokenStore? tokenStore = null,
        IVtubeStudioWebSocketFactory? socketFactory = null,
        RuntimeLeaseCoordinator? leases = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _tokenStore = tokenStore
            ?? (_options.TokenPath is null
                ? new NullVtubeStudioTokenStore()
                : new FileVtubeStudioTokenStore(_options.TokenPath));
        _socketFactory = socketFactory ?? new ClientWebSocketFactory();
        _leases = leases;
    }

    public async Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var snapshot = await ReadCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Catalog;
    }

    public async Task TriggerAsync(
        IReadOnlyList<string> catalogCodes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var requested = NormalizeCodes(catalogCodes);
        var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var code in requested)
            {
                var current = await ReadCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (!current.Actions.TryGetValue(code, out var action))
                {
                    throw new VtubeStudioProviderException("unknown_vts_code", retryable: false);
                }

                var data = action.Kind == "hotkey"
                    ? new JsonObject { ["hotkeyID"] = action.Identifier }
                    : new JsonObject
                    {
                        ["expressionFile"] = action.Identifier,
                        ["active"] = true,
                        ["fadeTime"] = 0.25,
                    };
                _ = await SendRequestAsync(
                    action.Kind == "hotkey" ? "HotkeyTriggerRequest" : "ExpressionActivationRequest",
                    data,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ResetSocketAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<VtubeStudioCatalog> ReadCatalogSnapshotAsync(CancellationToken cancellationToken)
    {
        var model = await SendRequestAsync("CurrentModelRequest", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
        if (!VtubeStudioCatalog.IsLoaded(model))
        {
            throw new VtubeStudioProviderException("vts_catalog_unavailable", retryable: true);
        }

        var hotkeys = await SendRequestAsync("HotkeysInCurrentModelRequest", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
        var expressions = await SendRequestAsync(
                "ExpressionStateRequest",
                new JsonObject { ["details"] = false, ["expressionFile"] = string.Empty },
                cancellationToken)
            .ConfigureAwait(false);
        return VtubeStudioCatalog.Parse(model, hotkeys, expressions);
    }

    private async Task<JsonObject> SendRequestAsync(
        string messageType,
        JsonObject data,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                var socket = await EnsureAuthenticatedLockedAsync(timeout.Token).ConfigureAwait(false);
                return await SendRequestLockedAsync(socket, messageType, data, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ResetSocketAsync().ConfigureAwait(false);
                throw new VtubeStudioProviderException("vts_timeout", retryable: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (VtubeStudioProviderException)
            {
                await ResetSocketAsync().ConfigureAwait(false);
                throw;
            }
            catch (WebSocketException error)
            {
                await ResetSocketAsync().ConfigureAwait(false);
                throw new VtubeStudioProviderException("vts_unavailable", retryable: true, error);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IVtubeStudioWebSocket> EnsureAuthenticatedLockedAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null)
        {
            return _socket;
        }

        IVtubeStudioWebSocket socket;
        try
        {
            socket = await _socketFactory.ConnectAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VtubeStudioProviderException("vts_timeout", retryable: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebSocketException error)
        {
            throw new VtubeStudioProviderException("vts_unavailable", retryable: true, error);
        }

        try
        {
            var token = await _tokenStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                var authenticated = await SendRequestOnSocketAsync(
                    socket,
                    "AuthenticationRequest",
                    new JsonObject
                    {
                        ["pluginName"] = _options.PluginName,
                        ["pluginDeveloper"] = _options.PluginDeveloper,
                        ["authenticationToken"] = token,
                    },
                    "AuthenticationResponse",
                    cancellationToken).ConfigureAwait(false);
                if (ReadAuthenticated(authenticated))
                {
                    _socket = socket;
                    return socket;
                }
            }

            var tokenResponse = await SendRequestOnSocketAsync(
                socket,
                "AuthenticationTokenRequest",
                new JsonObject
                {
                    ["pluginName"] = _options.PluginName,
                    ["pluginDeveloper"] = _options.PluginDeveloper,
                },
                "AuthenticationTokenResponse",
                cancellationToken).ConfigureAwait(false);
            var newToken = ReadToken(tokenResponse);
            await _tokenStore.WriteAsync(newToken, cancellationToken).ConfigureAwait(false);
            var final = await SendRequestOnSocketAsync(
                socket,
                "AuthenticationRequest",
                new JsonObject
                {
                    ["pluginName"] = _options.PluginName,
                    ["pluginDeveloper"] = _options.PluginDeveloper,
                    ["authenticationToken"] = newToken,
                },
                "AuthenticationResponse",
                cancellationToken).ConfigureAwait(false);
            if (!ReadAuthenticated(final))
            {
                throw new VtubeStudioProviderException("vts_auth_failed", retryable: false);
            }

            _socket = socket;
            return socket;
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonObject> SendRequestLockedAsync(
        IVtubeStudioWebSocket socket,
        string messageType,
        JsonObject data,
        CancellationToken cancellationToken)
    {
        var responseType = messageType switch
        {
            "CurrentModelRequest" => "CurrentModelResponse",
            "HotkeysInCurrentModelRequest" => "HotkeysInCurrentModelResponse",
            "ExpressionStateRequest" => "ExpressionStateResponse",
            "HotkeyTriggerRequest" => "HotkeyTriggerResponse",
            "ExpressionActivationRequest" => "ExpressionActivationResponse",
            _ => throw new VtubeStudioProviderException("invalid_request", retryable: false),
        };
        return await SendRequestOnSocketAsync(
                socket,
                messageType,
                data,
                responseType,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonObject> SendRequestOnSocketAsync(
        IVtubeStudioWebSocket socket,
        string messageType,
        JsonObject data,
        string responseType,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["apiName"] = ApiName,
            ["apiVersion"] = ApiVersion,
            ["requestID"] = Guid.NewGuid().ToString("N"),
            ["messageType"] = messageType,
            ["data"] = data,
        };
        var serialized = request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (StrictUtf8.GetByteCount(serialized) > _options.MaxMessageBytes)
        {
            throw new VtubeStudioProviderException("request_too_large", retryable: false);
        }

        await socket.SendTextAsync(serialized, cancellationToken).ConfigureAwait(false);
        var response = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ReadString(response["apiName"]), ApiName, StringComparison.Ordinal)
            || !string.Equals(ReadString(response["messageType"]), responseType, StringComparison.Ordinal))
        {
            throw new VtubeStudioProviderException("malformed_response", retryable: false);
        }

        return response;
    }

    private async Task<JsonObject> ReceiveJsonAsync(
        IVtubeStudioWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                throw new VtubeStudioProviderException("vts_unavailable", retryable: true);
            }

            if (frame.MessageType != WebSocketMessageType.Text)
            {
                throw new VtubeStudioProviderException("malformed_response", retryable: false);
            }

            if (buffer.Length + frame.Payload.Length > _options.MaxMessageBytes)
            {
                throw new VtubeStudioProviderException("response_too_large", retryable: false);
            }

            buffer.Write(frame.Payload.Span);
            if (!frame.EndOfMessage)
            {
                continue;
            }

            try
            {
                return JsonNode.Parse(StrictUtf8.GetString(buffer.ToArray())) as JsonObject
                    ?? throw new JsonException();
            }
            catch (Exception error) when (error is DecoderFallbackException or JsonException or InvalidOperationException)
            {
                throw new VtubeStudioProviderException("malformed_response", retryable: false, error);
            }
        }
    }

    private async Task<IAsyncDisposable?> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        if (_leases is null)
        {
            return null;
        }

        return await _leases.AcquireAsync(ResourceKind.VtubeStudio, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetSocketAsync()
    {
        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> NormalizeCodes(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count is < 1 or > RuntimeLimits.MaxVtsActionCodes)
        {
            throw new ArgumentException("VTube Studio action count is invalid.", nameof(codes));
        }

        var normalized = codes.Select(code =>
        {
            if (string.IsNullOrWhiteSpace(code)
                || code.Length > RuntimeLimits.MaxVtsCodeChars
                || code.Any(char.IsControl))
            {
                throw new ArgumentException("VTube Studio action code is invalid.", nameof(codes));
            }

            return code.Trim().ToLowerInvariant();
        }).ToArray();
        return normalized.Distinct(StringComparer.Ordinal).Count() == normalized.Length
            ? normalized
            : throw new VtubeStudioProviderException("unknown_vts_code", retryable: false);
    }

    private static bool ReadAuthenticated(JsonObject response) =>
        response["data"] is JsonObject data
        && data["authenticated"] is JsonValue value
        && value.TryGetValue<bool>(out var authenticated)
        && authenticated;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string ReadToken(JsonObject response)
    {
        if (response["data"] is not JsonObject data
            || data["authenticationToken"] is not JsonValue value
            || !value.TryGetValue<string>(out var token)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 4_096
            || token.Any(char.IsControl))
        {
            throw new VtubeStudioProviderException("vts_auth_failed", retryable: false);
        }

        return token;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
