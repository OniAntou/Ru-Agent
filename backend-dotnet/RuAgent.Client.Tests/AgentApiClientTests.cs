using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    private static PublicHostSession Session(string bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    [Fact]
    public async Task GetRunStrictParsingRejectsUnknownFields()
    {
        var s = Session();
        var handler = new FakeHandler(async req =>
        {
            Assert.Contains("/agent/v1/runs/run_", req.RequestUri!.ToString());
            var payload = new { run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", workspace_id = "ws_123", status = "running", preset = "standard", policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 0, last_sequence = 1, created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), unknown = "field" };
            var json = JsonSerializer.Serialize(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        await Assert.ThrowsAsync<AgentApiException>(() => client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public async Task GetRunStrictParsingAcceptsOptionalDurableProviderPinsAndLegacyAbsence()
    {
        var body = JsonSerializer.Serialize(RunSummary("OpenAI", "gpt-5"));
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            })));

        var pinned = await client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        body = """{"run_id":"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","workspace_id":"ws_test","status":"running","history_wait_kind":null,"preset":"standard","policy_snapshot_version":1,"policy_snapshot_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","version":2,"last_sequence":4,"created_at":"2026-10-04T00:00:00Z","updated_at":"2026-10-04T00:00:01Z","terminal_at":null,"terminal_error_code":null}""";
        var legacy = await client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var maxBackend = ProviderCredential.ModelsDevPrefix + new string('a', 64);
        body = JsonSerializer.Serialize(RunSummary(maxBackend, "gpt-5"));
        var maxModelsDevBackend = await client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal("OpenAI", pinned.ProviderBackend);
        Assert.Equal("gpt-5", pinned.ProviderModel);
        Assert.Equal("chat_completions", pinned.ProviderProtocol);
        Assert.Equal("native", pinned.ProviderCallingMode);
        Assert.Equal(128_000, pinned.ContextWindowTokens);
        Assert.Equal(8192, pinned.OutputTokenReserve);
        Assert.Null(legacy.ProviderBackend);
        Assert.Null(legacy.ProviderModel);
        Assert.Equal(maxBackend, maxModelsDevBackend.ProviderBackend);
    }

    [Fact]
    public async Task GetRunStrictParsingRejectsInvalidProviderPinShape()
    {
        foreach (var payload in new[]
        {
            RunSummary(1, "gpt-5"),
            RunSummary(null, new string('m', 257)),
        })
        {
            var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ => Response(payload))));
            var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.Equal("invalid_response", error.ErrorCode);
        }
    }

    [Fact]
    public async Task SetSessionPresetConflictParsesExactSessionAndCurrentVersion()
    {
        const string sessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"error_code\":\"preset_version_conflict\",\"message\":\"conflict\",\"session_id\":\"" + sessionId + "\",\"current_version\":17}",
                    Encoding.UTF8,
                    "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.SetSessionPresetAsync(sessionId, "standard", 0, interactive: true));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("preset_version_conflict", error.ErrorCode);
        Assert.Equal(sessionId, error.PresetSessionId);
        Assert.Equal(17, error.CurrentPresetVersion);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"error_code\":\"preset_version_conflict\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"current_version\":\"17\"}")]
    public async Task SetSessionPresetMalformedConflictMetadataRemainsUntrusted(string body)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.SetSessionPresetAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard", 0, interactive: true));

        Assert.Equal(
            body.StartsWith("{", StringComparison.Ordinal) ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" : null,
            error.PresetSessionId);
        Assert.Null(error.CurrentPresetVersion);
    }

    [Fact]
    public async Task GetRunStrictParsingRejectsInvalidProviderMetadata()
    {
        foreach (var metadata in new object[]
        {
            new { provider_calling_mode = "fallback" },
            new { context_window_tokens = 0 },
            new { output_token_reserve = 128001 },
        })
        {
            var root = JsonNode.Parse(JsonSerializer.Serialize(RunSummary("OpenAI", "gpt-5")))!.AsObject();
            foreach (var property in JsonSerializer.SerializeToNode(metadata)!.AsObject())
                root[property.Key] = property.Value?.DeepClone();
            var json = root.ToJsonString();
            var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                })));
            var error = await Assert.ThrowsAsync<AgentApiException>(() =>
                client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.Equal("invalid_response", error.ErrorCode);
        }
    }

    private static object RunSummary(object? backendPin, object? modelPin) => new
    {
        run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        workspace_id = "ws_test",
        status = "running",
        history_wait_kind = (string?)null,
        preset = "standard",
        policy_snapshot_version = 1,
        policy_snapshot_sha256 = new string('a', 64),
        version = 2L,
        last_sequence = 4L,
        created_at = "2026-10-04T00:00:00Z",
        updated_at = "2026-10-04T00:00:01Z",
        terminal_at = (string?)null,
        terminal_error_code = (string?)null,
        provider_backend = backendPin,
        provider_model = modelPin,
        provider_protocol = "chat_completions",
        provider_model_label = "GPT-5",
        provider_calling_mode = "native",
        context_window_tokens = 128_000,
        context_window_source = "provider_catalog",
        context_window_version = "catalog-v1",
        output_token_reserve = 8192,
    };

    private static object CreateRunSummary(object? backendPin, object? modelPin) => new
    {
        run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        workspace_id = "ws_test",
        status = "running",
        preset = "standard",
        created_at = "2026-10-04T00:00:00Z",
        updated_at = "2026-10-04T00:00:01Z",
        provider_backend = backendPin,
        provider_model = modelPin,
    };

    [Fact]
    public async Task GetPayloadRunBindingEnforced()
    {
        var s = Session();
        var handler = new FakeHandler(async _ =>
        {
            var json = JsonSerializer.Serialize(new { run_id = "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", payload_id = "payload_cccccccccccccccccccccccccccccccc", kind = "user_prompt", encoding = "utf8_text", content = "hello" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.GetPayloadAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "payload_cccccccccccccccccccccccccccccccc"));
        Assert.Equal("payload_run_mismatch", ex.ErrorCode);
    }

    [Theory]
    [InlineData("assistanttext", "utf8text")]
    [InlineData("assistantstep", "json")]
    public async Task GetPayloadContent_preserves_host_metadata_and_verbatim_json(string kind, string encoding)
    {
        const string answer = "{\"tool_calls\":[],\"finish_reason\":\"stop\"}";
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var json = JsonSerializer.Serialize(new
            {
                run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                payload_id = "payload_cccccccccccccccccccccccccccccccc",
                kind, encoding, content = answer,
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        });
        using var client = new AgentApiClient(Session(), new HttpClient(handler));

        var payload = await client.GetPayloadContentAsync(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "payload_cccccccccccccccccccccccccccccccc");

        Assert.Equal(new AgentPayloadContent(kind, encoding, answer), payload);
    }

    [Fact]
    public async Task CreateRunUsesBearerAndStrictDto()
    {
        var bearer = new string('d', 64);
        var s = Session(bearer);
        var handler = new FakeHandler(async req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(bearer, req.Headers.Authorization?.Parameter);
            var resp = new CreateAgentRunResponse("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "ws_1", "waiting_history", "standard", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(JsonSerializer.Serialize(resp), Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var req = new CreateAgentRunRequest("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "D:\\LLM", "hello", "standard", 1, "idem-key-123");
        var resp = await client.CreateRunAsync(req);
        Assert.Equal("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", resp.RunId);
    }

    [Fact]
    public async Task CreateRun_replays_same_serialized_request_and_idempotency_key_after_lost_acknowledgement()
    {
        var payloads = new List<string>();
        var idempotencyKeys = new List<string?>();
        var committedRuns = new Dictionary<string, CreateAgentRunResponse>(StringComparer.Ordinal);
        var firstRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new CreateAgentRunResponse(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ws_1",
            "running",
            "standard",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"));
        var handler = new FakeHandler(async request =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync());
            using (var document = JsonDocument.Parse(payloads[^1]))
            {
                idempotencyKeys.Add(document.RootElement.GetProperty("idempotency_key").GetString());
            }

            if (payloads.Count == 1)
            {
                committedRuns.Add(idempotencyKeys[^1]!, run);
                firstRequestReceived.TrySetResult();
                await loseFirstResponse.Task;
                throw new HttpRequestException("response lost after commit");
            }

            Assert.True(committedRuns.TryGetValue(idempotencyKeys[^1]!, out var committedRun));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(committedRun), Encoding.UTF8, "application/json"),
            };
        });
        var client = new AgentApiClient(Session(), new HttpClient(handler));
        var mutableImages = new List<string> { "data:image/png;base64,first" };
        var request = new CreateAgentRunRequest("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "D:\\LLM", "hello", "standard", 1, "idem-replay", mutableImages);

        var createTask = client.CreateRunAsync(request);
        await firstRequestReceived.Task;
        mutableImages[0] = "data:image/png;base64,changed";
        loseFirstResponse.SetResult();
        var response = await createTask;

        Assert.Equal("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", response.RunId);
        Assert.Equal(2, payloads.Count);
        Assert.Equal(payloads[0], payloads[1]);
        Assert.Equal(new[] { "idem-replay", "idem-replay" }, idempotencyKeys);
    }

    [Fact]
    public async Task CreateRun_reports_unknown_outcome_after_bounded_replay_is_exhausted()
    {
        var attempts = 0;
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler((HttpRequestMessage _) =>
        {
            attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("acknowledgement unavailable"));
        })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("agent_create_outcome_unknown", error.ErrorCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CreateRun_preserves_create_uncertainty_when_replay_returns_an_http_error()
    {
        var attempts = 0;
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler((HttpRequestMessage _) =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("first acknowledgement lost"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error_code\":\"invalid_workspace\"}", Encoding.UTF8, "application/json"),
                });
        })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_workspace", error.ErrorCode);
        Assert.True(error.IsCreateOutcomeUnknown);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CreateRun_marks_server_error_as_potentially_ambiguous()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error_code\":\"host_unavailable\"}", Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("host_unavailable", error.ErrorCode);
        Assert.True(error.IsCreateOutcomeUnknown);
    }

    [Fact]
    public async Task CreateRun_marks_strict_success_parse_failure_as_potentially_ambiguous()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"run_id\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"unexpected\":true}", Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("unknown_field", error.ErrorCode);
        Assert.True(error.IsCreateOutcomeUnknown);
    }

    [Fact]
    public async Task CreateRun_continues_acknowledgement_replay_after_the_caller_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var run = new CreateAgentRunResponse(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ws_1",
            "running",
            "standard",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"));
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler((HttpRequestMessage _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                cancellation.Cancel();
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("response lost after caller cancellation"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(run), Encoding.UTF8, "application/json"),
            });
        })));

        var result = await client.CreateRunAsync(
            Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard"), cancellation.Token);

        Assert.Equal("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.RunId);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CreateRunStrictParsingAcceptsOptionalProviderPins()
    {
        var response = new CreateAgentRunResponse(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ws_1",
            "waiting_history",
            "standard",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            ProviderBackend: "OpenAI",
            ProviderModel: "gpt-5");
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
            })));

        var result = await client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard"));

        Assert.Equal("OpenAI", result.ProviderBackend);
        Assert.Equal("gpt-5", result.ProviderModel);
    }

    [Fact]
    public async Task CreateRunStrictParsingRejectsInvalidProviderPinShape()
    {
        foreach (var payload in new[]
        {
            CreateRunSummary(1, "gpt-5"),
            CreateRunSummary(null, new string('m', 257)),
        })
        {
            var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ => Response(payload))));
            var error = await Assert.ThrowsAsync<AgentApiException>(() =>
                client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));
            Assert.Equal("invalid_response", error.ErrorCode);
        }
    }

    [Fact]
    public async Task CreateRun_rejects_wrong_session_before_returning_response()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(async _ => Response(new
        {
            run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            session_id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            workspace_id = "ws_test",
            status = "running",
            preset = "standard",
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        }))));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task CreateRun_rejects_wrong_preset_before_returning_response()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(async _ => Response(new
        {
            run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            workspace_id = "ws_test",
            status = "running",
            preset = "plan",
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        }))));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    public async Task CreateRun_rejects_missing_or_unsafe_status_before_returning_response(string status)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(async _ => Response(new
        {
            run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            workspace_id = "ws_test",
            status,
            preset = "standard",
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        }))));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task CreateRun_rejects_missing_workspace_id_before_returning_response()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(async _ => Response(new
        {
            run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            status = "running",
            preset = "standard",
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        }))));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task CreateRun_rejects_waiting_trust_without_known_challenge_ids()
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(async _ => Response(new
        {
            run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            workspace_id = "ws_test",
            status = "waiting_workspace_trust",
            preset = "standard",
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        }))));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "standard")));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task CancelRejectsWrongBearer()
    {
        var s = Session();
        var handler = new FakeHandler(async _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error_code\":\"unauthorized\"}") });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.CancelAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal("unauthorized", ex.ErrorCode);
    }

    [Fact]
    public async Task Client_rejects_non_hex_generated_ids_before_network_io()
    {
        var s = Session();
        var handler = new FakeHandler(async _ => throw new InvalidOperationException("network must not be called"));
        var client = new AgentApiClient(s, new HttpClient(handler));

        var runError = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GetRunAsync("run_gggggggggggggggggggggggggggggggg"));
        var payloadError = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GetPayloadAsync(
                "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "payload_gggggggggggggggggggggggggggg"));

        Assert.Equal("invalid_run_id", runError.ErrorCode);
        Assert.Equal("invalid_payload_id", payloadError.ErrorCode);
    }

    [Fact]
    public async Task CreateRun_preserves_backend_error_code_for_bad_requests()
    {
        var s = Session();
        var handler = new FakeHandler(async _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error_code\":\"invalid_workspace\"}", Encoding.UTF8, "application/json")
        });
        var client = new AgentApiClient(s, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(
            new CreateAgentRunRequest(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "D:\\missing",
                "hello",
                "standard",
                1,
                "idem-key-123")));

        Assert.Equal("invalid_workspace", ex.ErrorCode);
        Assert.False(ex.IsCreateOutcomeUnknown);
    }

    [Fact]
    public async Task Client_rejects_an_oversized_http_response_before_json_parsing()
    {
        var session = Session();
        var handler = new FakeHandler(async _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 8 * 1024 * 1024 + 1), Encoding.UTF8, "application/json"),
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateRunAsync(
            new CreateAgentRunRequest(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "D:\\LLM",
                "hello",
                "standard",
                1,
                "idem-key-123")));

        Assert.Equal("response_too_large", error.ErrorCode);
        Assert.True(error.IsCreateOutcomeUnknown);
    }

    [Fact]
    public async Task Compact_sends_bearer_and_parses_metadata_only_response()
    {
        var bearer = new string('e', 64);
        var session = Session(bearer);
        var handler = new FakeHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith(
                "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact",
                request.RequestUri!.ToString(),
                StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(bearer, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"run_id\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"status\":\"running\",\"strategy\":\"aggressive\",\"compaction_id\":\"compact-1\",\"before_tokens\":100,\"after_tokens\":40,\"run_version\":2}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var result = await client.CompactAsync(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new CompactAgentRunRequest("aggressive"));

        Assert.Equal("compact-1", result.CompactionId);
        Assert.Equal(40, result.AfterTokens);
    }

    [Fact]
    public async Task DispatchCommand_sends_typed_text_and_parses_bounded_result()
    {
        var session = Session(new string('f', 64));
        var handler = new FakeHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith(
                "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/commands",
                request.RequestUri!.ToString(),
                StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"run_id\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"status\":\"succeeded\",\"code\":\"help\",\"data\":{\"commands\":[]},\"run_version\":3}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var result = await client.DispatchCommandAsync(
            "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "/help");

        Assert.Equal("help", result.Code);
        Assert.Equal(3, result.RunVersion);
    }

    [Theory]
    [InlineData("once", "allow_once")]
    [InlineData("session", "allow_session_directory")]
    [InlineData("allow_session_directory", "allow_session_directory")]
    [InlineData("bogus", "allow_once")]
    public async Task Approve_maps_scope_to_exactly_one_server_command(string scope, string expectedCommand)
    {
        var session = Session();
        string? sentCommand = null;
        var handler = new FakeHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            sentCommand = JsonDocument.Parse(body).RootElement.GetProperty("command").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        await client.ApproveAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", scope);

        Assert.Equal(expectedCommand, sentCommand);
    }

    [Fact]
    public async Task SetSessionPreset_sends_client_snapshot_token_before_mutation()
    {
        var session = Session();
        var token = "sps_" + new string('a', 32);
        var handler = new FakeHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("session_preset", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(token, document.RootElement.GetProperty("snapshot_token").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"version\":1,\"snapshot_token\":\"" + token + "\"}", Encoding.UTF8, "application/json"),
            };
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var response = await client.SetSessionPresetAsync(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "standard",
            0,
            false,
            CancellationToken.None,
            token);

        Assert.Equal(1, response.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task RestoreSessionPreset_uses_existing_policy_endpoint_and_token()
    {
        var session = Session();
        var token = "sps_" + new string('b', 32);
        var handler = new FakeHandler(async request =>
        {
            Assert.EndsWith("/agent/v1/policies", request.RequestUri!.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("session_preset_restore", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(token, document.RootElement.GetProperty("snapshot_token").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json"),
            };
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var response = await client.RestoreSessionPresetAsync(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            token);

        Assert.True(response.GetProperty("restored").GetBoolean());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("invalid_preset")]
    [InlineData("version_mismatch")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("invalid_json")]
    public async Task RestoreSessionPreset_rejects_invalid_shape(string mode)
    {
        var session = Session();
        var token = "sps_" + new string('c', 32);
        var handler = new FakeHandler(async _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(RestoreBody(mode), Encoding.UTF8, "application/json")
        });
        var client = new AgentApiClient(session, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            client.RestoreSessionPresetAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", token));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    private static string RestoreBody(string mode)
    {
        const string session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var valid = $"{{\"status\":\"ok\",\"session_id\":\"{session}\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}}";
        return mode switch
        {
            "unknown" => valid[..^1] + ",\"extra\":true}",
            "duplicate" => valid[..^1] + ",\"status\":\"ok\"}",
            "invalid_preset" => $"{{\"status\":\"ok\",\"session_id\":\"{session}\",\"preset\":\"invalid\",\"version\":999,\"restored\":true,\"replayed\":false}}",
            "version_mismatch" => $"{{\"status\":\"ok\",\"session_id\":\"{session}\",\"preset\":\"standard\",\"version\":0,\"restored\":true,\"replayed\":false}}",
            "malformed" => JsonSerializer.Serialize(new { status = "ok", session_id = session, preset = 1, version = "1", restored = true, replayed = false }),
            "invalid_json" => "{",
            _ => valid.Replace(",\"replayed\":false", string.Empty, StringComparison.Ordinal),
        };
    }

    private static CreateAgentRunRequest Request(string sessionId, string preset) =>
        new(sessionId, "D:\\LLM", "hello", preset, 1, "idem-key-123");

    private static HttpResponseMessage Response(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
