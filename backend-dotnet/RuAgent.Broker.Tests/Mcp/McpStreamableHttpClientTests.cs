using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Mcp;
using Xunit;

namespace RuAgent.Broker.Tests.Mcp;

public sealed class McpStreamableHttpClientTests : IDisposable
{
    private const string BearerToken = "test-bearer-token-123";
    private readonly string _home = Directory.CreateTempSubdirectory("ru-mcp-http").FullName;

    public McpStreamableHttpClientTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".ru"));
        File.WriteAllText(McpConfiguration.PathFor(_home), """
            {"servers":{"remote":{"transport":"streamable_http","url":"https://mcp.example.test/mcp","bearer_credential":"ru-mcp-http-token","timeout_seconds":2}}}
            """);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private McpServerHost Host(HttpMessageHandler handler, Func<string, string?>? credential = null) =>
        new(() => _home,
            credential ?? (_ => BearerToken),
            (_, _, _) => throw new InvalidOperationException("HTTP configuration must not start a process."),
            _ => handler);

    [Fact]
    public async Task Configured_server_posts_json_and_propagates_negotiated_session_headers()
    {
        var requests = new List<(string Method, string? Session, string? Protocol, string? Authorization, string Accept)>();
        var credentialReads = 0;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            requests.Add((method,
                request.Headers.TryGetValues("MCP-Session-Id", out var sessions) ? sessions.Single() : null,
                request.Headers.TryGetValues("MCP-Protocol-Version", out var versions) ? versions.Single() : null,
                request.Headers.Authorization?.ToString(),
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType))));
            return method switch
            {
                "initialize" => RpcResponse(body["id"], InitializeResult(), "session-1"),
                "notifications/initialized" => Accepted(),
                "tools/list" => RpcResponse(body["id"], ToolsResult()),
                _ => throw new InvalidOperationException($"Unexpected method {method}."),
            };
        });
        await using var host = Host(handler, _ =>
        {
            credentialReads++;
            return BearerToken;
        });

        var listed = host.ListServers();
        Assert.Empty(requests);
        Assert.Equal("streamable_http", listed["servers"]![0]!["transport"]!.GetValue<string>());
        Assert.Equal(["tools/list", "tools/call"],
            listed["servers"]![0]!["client_capabilities"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.True(listed["servers"]![0]!["authentication"]!["credential_configured"]!.GetValue<bool>());
        Assert.DoesNotContain("ru-mcp-http-token", listed.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0, credentialReads);

        var tools = await host.ListToolsAsync("remote", CancellationToken.None);

        Assert.Equal("echo", tools["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(1, credentialReads);
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], requests.Select(request => request.Method));
        Assert.Null(requests[0].Session);
        Assert.Null(requests[0].Protocol);
        Assert.All(requests, request => Assert.Equal("Bearer " + BearerToken, request.Authorization));
        Assert.Contains("application/json", requests[0].Accept, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text/event-stream", requests[0].Accept, StringComparison.OrdinalIgnoreCase);
        Assert.All(requests.Skip(1), request =>
        {
            Assert.Equal("session-1", request.Session);
            Assert.Equal(McpStreamableHttpClient.ProtocolVersion, request.Protocol);
        });
    }

    [Fact]
    public async Task Streamable_http_sse_response_is_parsed_without_legacy_transport_fallback()
    {
        var methods = new List<string>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            methods.Add(method);
            if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
            if (method == "notifications/initialized") return Accepted();
            if (method == "tools/list")
            {
                using var envelope = RpcResponse(body["id"], ToolsResult());
                var json = await envelope.Content!.ReadAsStringAsync(cancellationToken);
                var sse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PrefixThenBlockingStream(Encoding.UTF8.GetBytes("data: " + json + "\n\n"))),
                };
                sse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return sse;
            }

            throw new InvalidOperationException($"Unexpected method {method}.");
        });
        await using var host = Host(handler);

        var result = await host.ListToolsAsync("remote", CancellationToken.None);

        Assert.Equal("echo", result["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], methods);
    }

    [Fact]
    public async Task Unsupported_negotiated_version_fails_without_retrying_another_protocol()
    {
        var methods = new List<string>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            methods.Add(method);
            if (method == "initialize")
            {
                var result = InitializeResult();
                result["protocolVersion"] = "2025-06-18";
                return RpcResponse(body["id"], result);
            }

            throw new InvalidOperationException($"Unexpected protocol fallback method {method}.");
        });
        await using var host = Host(handler);

        var error = await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("remote", CancellationToken.None));

        Assert.Equal("mcp_protocol_version_unsupported", error.Code);
        Assert.Equal(["initialize"], methods);
    }

    [Fact]
    public async Task Bearer_unauthorized_is_named_and_never_follows_redirects()
    {
        var requests = 0;
        var unauthorized = new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        await using (var host = Host(unauthorized))
        {
            var error = await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("remote", CancellationToken.None));
            Assert.Equal("mcp_auth_required", error.Code);
            Assert.Contains("authentication", error.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(BearerToken, error.Detail, StringComparison.Ordinal);
            Assert.Equal(1, requests);
        }

        requests = 0;
        var redirect = new DelegateHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://other.example.test/mcp");
            return Task.FromResult(response);
        });
        await using var redirectHost = Host(redirect);
        var redirectError = await Assert.ThrowsAsync<McpException>(() => redirectHost.ListToolsAsync("remote", CancellationToken.None));
        Assert.Equal("mcp_redirect_rejected", redirectError.Code);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Missing_bearer_credential_fails_before_any_http_request()
    {
        var requests = 0;
        await using var host = Host(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }), _ => null);

        var error = await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("remote", CancellationToken.None));

        Assert.Equal("mcp_credential_missing", error.Code);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Json_rpc_server_error_is_named_and_credential_redacted()
    {
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
            if (method == "notifications/initialized") return Accepted();
            return RpcErrorResponse(body["id"], "server rejected " + BearerToken);
        });
        await using var host = Host(handler);

        var error = await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("remote", "echo", "{}", CancellationToken.None));

        Assert.Equal("mcp_error", error.Code);
        Assert.Contains("[redacted]", error.Detail);
        Assert.DoesNotContain(BearerToken, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_404_is_not_replayed_and_a_later_list_starts_a_new_session()
    {
        var methods = new List<string>();
        var initializeCount = 0;
        var callCount = 0;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            methods.Add(method);
            if (method == "initialize")
            {
                initializeCount++;
                return RpcResponse(body["id"], InitializeResult(), "session-" + initializeCount);
            }
            if (method == "notifications/initialized") return Accepted();
            if (method == "tools/list") return RpcResponse(body["id"], ToolsResult());
            if (method == "tools/call")
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException($"Unexpected method {method}.");
        });
        await using var host = Host(handler);
        await host.ListToolsAsync("remote", CancellationToken.None);

        var expired = await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("remote", "echo", "{}", CancellationToken.None));
        Assert.Equal("mcp_session_expired", expired.Code);
        var notReplayed = await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("remote", "echo", "{}", CancellationToken.None));
        Assert.Equal("mcp_session_expired", notReplayed.Code);
        Assert.Equal(1, callCount);

        await host.ListToolsAsync("remote", CancellationToken.None);

        Assert.Equal(2, initializeCount);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Tool_text_structured_content_and_resource_link_metadata_are_redacted_and_preserved()
    {
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            if (method == "initialize") return RpcResponse(body["id"], InitializeResult(), "session-1");
            if (method == "notifications/initialized") return Accepted();
            if (method == "tools/list") return RpcResponse(body["id"], new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "echo",
                    ["description"] = "Echo",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["value"] = new JsonObject { ["type"] = "string", ["description"] = BearerToken },
                        },
                    },
                }),
            });
            if (method == "tools/call")
            {
                return RpcResponse(body["id"], new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "message " + BearerToken },
                        new JsonObject { ["type"] = "resource_link", ["uri"] = "https://example.test/?token=" + BearerToken,
                            ["name"] = "report", ["title"] = "Details", ["mimeType"] = "text/plain", ["size"] = 42 },
                        new JsonObject { ["type"] = "image", ["data"] = "must-not-be-returned" }),
                    ["structuredContent"] = new JsonObject { ["value"] = BearerToken },
                });
            }

            throw new InvalidOperationException($"Unexpected method {method}.");
        });
        await using var host = Host(handler);

        var listedTools = await host.ListToolsAsync("remote", CancellationToken.None);
        Assert.Equal("[redacted]", listedTools["tools"]![0]!["input_schema"]!["properties"]!["value"]!["description"]!.GetValue<string>());

        var result = await host.CallToolAsync("remote", "echo", "{}", CancellationToken.None);

        Assert.Equal("message [redacted]", result["output"]!.GetValue<string>());
        Assert.Equal("https://example.test/?token=[redacted]", result["resource_links"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("report", result["resource_links"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("[redacted]", result["structured_content"]!["value"]!.GetValue<string>());
        Assert.Contains("1 non-text", result["omitted_content"]!.GetValue<string>());
        Assert.DoesNotContain(BearerToken, result.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-returned", result.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_cancels_the_http_request_and_response_events_are_bounded()
    {
        var client = new McpStreamableHttpClient(new Uri("http://127.0.0.1:1/mcp"), null,
            new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                return body["method"]!.GetValue<string>() switch
                {
                    "initialize" => RpcResponse(body["id"], InitializeResult()),
                    "notifications/initialized" => Accepted(),
                    "tools/call" => await HangAsync(cancellationToken),
                    "notifications/cancelled" => Accepted(),
                    _ => throw new InvalidOperationException(),
                };
            }));
        await using (client)
        {
            await client.InitializeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            var timeout = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("echo", new JsonObject(),
                TimeSpan.FromMilliseconds(25), CancellationToken.None));
            Assert.Equal("mcp_timeout", timeout.Code);
        }

        var eventBody = "data: " + new string('x', McpStreamableHttpClient.MaxSseEventBytes + 1) + "\n\n";
        await using var eventClient = new McpStreamableHttpClient(new Uri("http://127.0.0.1:1/mcp"), null,
            new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                var method = body["method"]!.GetValue<string>();
                if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
                if (method == "notifications/initialized") return Accepted();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(eventBody, Encoding.UTF8, "text/event-stream"),
                };
            }));
        await eventClient.InitializeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var bound = await Assert.ThrowsAsync<McpException>(() => eventClient.CallToolAsync("echo", new JsonObject(),
            TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("mcp_output_too_large", bound.Code);

        var tooManyEvents = string.Concat(Enumerable.Repeat("data: {}\n\n", McpStreamableHttpClient.MaxSseEvents + 1));
        await using var eventCountClient = new McpStreamableHttpClient(new Uri("http://127.0.0.1:1/mcp"), null,
            new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                var method = body["method"]!.GetValue<string>();
                if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
                if (method == "notifications/initialized") return Accepted();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tooManyEvents, Encoding.UTF8, "text/event-stream"),
                };
            }));
        await eventCountClient.InitializeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var eventLimit = await Assert.ThrowsAsync<McpException>(() => eventCountClient.CallToolAsync("echo", new JsonObject(),
            TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("mcp_event_limit", eventLimit.Code);
    }

    [Fact]
    public async Task Caller_cancellation_aborts_a_post_without_becoming_a_timeout()
    {
        var callEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new List<string>();
        await using var client = new McpStreamableHttpClient(new Uri("http://127.0.0.1:1/mcp"), null,
            new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                var method = body["method"]!.GetValue<string>();
                methods.Add(method);
                return method switch
                {
                    "initialize" => RpcResponse(body["id"], InitializeResult()),
                    "notifications/initialized" => Accepted(),
                    "tools/call" => await WaitForCancellationAsync(callEntered, cancellationToken),
                    "notifications/cancelled" => Accepted(),
                    _ => throw new InvalidOperationException(),
                };
            }));
        await client.InitializeAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var call = client.CallToolAsync("echo", new JsonObject(), TimeSpan.FromSeconds(5), cancellation.Token);
        await callEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(["initialize", "notifications/initialized", "tools/call", "notifications/cancelled"], methods);
    }

    [Fact]
    public async Task Total_http_response_bytes_have_a_hard_limit()
    {
        var largeBody = new string('x', McpStreamableHttpClient.MaxResponseBytes + 1);
        await using var client = new McpStreamableHttpClient(new Uri("http://127.0.0.1:1/mcp"), null,
            new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                var method = body["method"]!.GetValue<string>();
                if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
                if (method == "notifications/initialized") return Accepted();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(largeBody, Encoding.UTF8, "application/json"),
                };
            }));
        await client.InitializeAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        var error = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("echo", new JsonObject(),
            TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.Equal("mcp_output_too_large", error.Code);
    }

    [Fact]
    public async Task Repeated_empty_pages_stop_at_the_tools_page_limit()
    {
        var pageRequests = 0;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await ReadBodyAsync(request, cancellationToken);
            var method = body["method"]!.GetValue<string>();
            if (method == "initialize") return RpcResponse(body["id"], InitializeResult());
            if (method == "notifications/initialized") return Accepted();
            if (method == "tools/list")
            {
                pageRequests++;
                return RpcResponse(body["id"], new JsonObject { ["tools"] = new JsonArray(), ["nextCursor"] = "same" });
            }

            throw new InvalidOperationException($"Unexpected method {method}.");
        });
        await using var host = Host(handler);

        var error = await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("remote", CancellationToken.None));

        Assert.Equal("mcp_page_limit", error.Code);
        Assert.Equal(McpStdioClient.MaxToolPages, pageRequests);
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)) as JsonObject
        ?? throw new InvalidOperationException("Expected an MCP JSON request.");

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = McpStreamableHttpClient.ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
    };

    private static JsonObject ToolsResult() => new()
    {
        ["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "echo",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        }),
    };

    private static HttpResponseMessage RpcResponse(JsonNode? id, JsonObject result, string? sessionId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result.DeepClone(),
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (sessionId is not null) response.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
        return response;
    }

    private static HttpResponseMessage RpcErrorResponse(JsonNode? id, string message) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
        }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted)
    {
        Content = new ByteArrayContent([]),
    };

    private static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The request should have been cancelled.");
    }

    private static async Task<HttpResponseMessage> WaitForCancellationAsync(
        TaskCompletionSource entered,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The request should have been cancelled.");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
