using System.Net;
using System.Text.Json;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcDispatchSurfaceTests
{
    private static async Task<RpcDispatcher> CreateInitializedAsync(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        List<string>? seen = null)
    {
        seen ??= [];
        var http = RpcTestHost.CreateClient(request =>
        {
            seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return handler(request);
        });
        var session = RpcTestHost.CreateSession();
        var dispatcher = new RpcDispatcher(
            session,
            new SessionApiClient(session, http),
            new AgentApiClient(session, http),
            new SettingsApiClient(session, http),
            runStarter: null!,
            runBridge: null!,
            log: TextWriter.Null,
            httpClient: http);
        await dispatcher.DispatchAsync(Req("initialize", "{\"protocolVersion\":1}"), CancellationToken.None);
        return dispatcher;
    }

    private static RpcRequest Req(string method, string paramsJson = "{}")
    {
        var line = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{paramsJson}}}";
        var parsed = RpcMessage.TryParseRequest(line, out var error);
        Assert.Null(error);
        return parsed!;
    }

    [Fact]
    public async Task Run_detach_falls_back_to_run_id_when_no_subscription_id_is_given()
    {
        // The bounded cleanup for a client whose attach response never arrived:
        // no bridge is wired here, so the answer is an explicit zero, not an error.
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));

        var result = await dispatcher.DispatchAsync(
            Req("run.detach", "{\"runId\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"),
            CancellationToken.None);

        Assert.Equal(0, result!["detached"]!.GetValue<int>());
        Assert.Equal("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result!["run_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_create_returns_the_session_id()
    {
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\",\"session_id\":\"sess_1\"}")), seen);
        var result = await dispatcher.DispatchAsync(Req("session.create"), CancellationToken.None);
        Assert.Equal("sess_1", result!["session_id"]!.GetValue<string>());
        Assert.Contains("POST /sessions/new", seen);
    }

    [Fact]
    public async Task Session_list_passes_through_the_envelope()
    {
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\",\"sessions\":[]}")), seen);
        var result = await dispatcher.DispatchAsync(Req("session.list"), CancellationToken.None);
        Assert.Equal("ok", result!["status"]!.GetValue<string>());
        Assert.Contains("GET /sessions", seen);
    }

    [Fact]
    public async Task Policy_remove_requires_explicit_confirmation()
    {
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("policy.remove", "{\"ruleId\":\"rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.InvalidParams, ex.RpcErrorCode);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task AgentApiException_from_a_route_surfaces_as_ruCode()
    {
        const string ruleId = "rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"error_code\":\"approval_stale\",\"message\":\"stale\"}"),
            }), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("policy.remove", $"{{\"ruleId\":\"{ruleId}\",\"confirm\":true}}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("approval_stale", ex.RuCode);
        Assert.Equal(409, ex.HttpStatus);
        Assert.Contains($"DELETE /agent/v1/policies/{ruleId}", seen);
    }

    [Fact]
    public async Task Session_rewind_posts_the_to_message_index()
    {
        string? body = null;
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return RpcTestHost.Json("{\"status\":\"ok\"}");
        }, seen);
        await dispatcher.DispatchAsync(
            Req("session.rewind", "{\"sessionId\":\"sess_1\",\"index\":3}"),
            CancellationToken.None);
        Assert.Contains("POST /sessions/sess_1/rewind", seen);
        Assert.Contains("\"to_message_index\":3", body);
    }

    [Fact]
    public async Task Policy_list_hits_the_policies_route_and_returns_an_array()
    {
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(
            RpcTestHost.Json("{\"status\":\"ok\",\"items\":[{\"rule_id\":\"rule_1\"},{\"rule_id\":\"rule_2\"}]}")), seen);
        var result = await dispatcher.DispatchAsync(Req("policy.list"), CancellationToken.None);
        Assert.Contains("GET /agent/v1/policies", seen);
        var items = result!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("rule_1", items[0]!["rule_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Policy_import_with_confirm_imports_each_rule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ruagent-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "rules": [
                {
                  "scope": "global",
                  "decision": "allow",
                  "matcher": { "tool_id": "read", "canonical_path_pattern": "src/app.ts" }
                },
                {
                  "scope": "project",
                  "decision": "deny",
                  "matcher": { "tool_id": "shell" }
                }
              ]
            }
            """);
        try
        {
            List<string> seen = [];
            var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(
                RpcTestHost.Json("{\"status\":\"ok\",\"rule_id\":\"rule_1\"}")), seen);
            var result = await dispatcher.DispatchAsync(
                Req("policy.import", $"{{\"file\":{JsonSerializer.Serialize(path)},\"confirm\":true}}"),
                CancellationToken.None);
            Assert.Equal(2, seen.Count(route => route == "POST /agent/v1/policies"));
            var imported = result!["imported"]!.AsArray();
            Assert.Equal(2, imported.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Run_start_rejects_an_unknown_preset_before_any_http_call()
    {
        var seen = new List<string>();
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.start", "{\"workspace\":\"C:\\\\w\",\"prompt\":\"p\",\"preset\":\"yolo\"}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.InvalidParams, ex.RpcErrorCode);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Run_start_requires_full_access_confirmation()
    {
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.start", "{\"workspace\":\"C:\\\\w\",\"prompt\":\"p\",\"preset\":\"full_access\"}"),
                CancellationToken.None));
        Assert.Equal("full_access_confirmation_required", ex.RuCode);
    }

    [Fact]
    public async Task Run_start_without_a_starter_fails_closed()
    {
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.start", "{\"workspace\":\"C:\\\\w\",\"prompt\":\"p\"}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.InternalError, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Run_status_serializes_the_run_response_with_snake_case_fields()
    {
        var runId = "run_" + new string('a', 32);
        var body = "{\"run_id\":\"" + runId + "\",\"session_id\":\"s1\",\"workspace_id\":\"w1\",\"status\":\"running\",\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1,\"policy_snapshot_sha256\":\"x\",\"version\":2,\"last_sequence\":9,\"created_at\":\"2026-09-29T00:00:00Z\",\"updated_at\":\"2026-09-29T00:00:00Z\",\"terminal_at\":null,\"terminal_error_code\":null}";
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(body)), seen);
        var result = await dispatcher.DispatchAsync(Req("run.status", $"{{\"runId\":\"{runId}\"}}"), CancellationToken.None);
        Assert.Equal("running", result!["status"]!.GetValue<string>());
        Assert.Equal(9, result["last_sequence"]!.GetValue<long>());
        Assert.Contains($"GET /agent/v1/runs/{runId}", seen);
    }

    [Fact]
    public async Task Run_output_returns_base64_bytes()
    {
        var runId = "run_" + new string('a', 32);
        var payloadId = "payload_" + new string('b', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(
            $"{{\"run_id\":\"{runId}\",\"payload_id\":\"{payloadId}\",\"kind\":\"tool_output\",\"encoding\":\"utf8text\",\"content\":\"hello\"}}")), seen);
        var result = await dispatcher.DispatchAsync(
            Req("run.output", $"{{\"runId\":\"{runId}\",\"payloadId\":\"{payloadId}\"}}"),
            CancellationToken.None);
        Assert.Equal("base64", result!["encoding"]!.GetValue<string>());
        Assert.Equal("aGVsbG8=", result["data"]!.GetValue<string>());
        Assert.Equal(5, result["byte_length"]!.GetValue<int>());
        Assert.Contains($"GET /agent/v1/runs/{runId}/payloads/{payloadId}", seen);
    }

    [Fact]
    public async Task Run_output_encodes_json_payloads_as_utf8_base64()
    {
        var runId = "run_" + new string('a', 32);
        var payloadId = "payload_" + new string('b', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(
            JsonSerializer.Serialize(new
            {
                run_id = runId,
                payload_id = payloadId,
                kind = "assistant_text",
                encoding = "json",
                content = "{\"answer\":1}",
            }))), seen);
        var result = await dispatcher.DispatchAsync(
            Req("run.output", $"{{\"runId\":\"{runId}\",\"payloadId\":\"{payloadId}\"}}"),
            CancellationToken.None);
        Assert.Equal("base64", result!["encoding"]!.GetValue<string>());
        Assert.Equal("eyJhbnN3ZXIiOjF9", result["data"]!.GetValue<string>());
        Assert.Equal(12, result["byte_length"]!.GetValue<int>());
        Assert.Contains($"GET /agent/v1/runs/{runId}/payloads/{payloadId}", seen);
    }

    [Fact]
    public async Task Run_output_decodes_opaque_payloads_without_double_encoding()
    {
        var runId = "run_" + new string('a', 32);
        var payloadId = "payload_" + new string('b', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(
            $"{{\"run_id\":\"{runId}\",\"payload_id\":\"{payloadId}\",\"kind\":\"tool_output\",\"encoding\":\"opaque\",\"content\":\"aGVsbG8=\"}}")), seen);
        var result = await dispatcher.DispatchAsync(
            Req("run.output", $"{{\"runId\":\"{runId}\",\"payloadId\":\"{payloadId}\"}}"),
            CancellationToken.None);
        Assert.Equal("base64", result!["encoding"]!.GetValue<string>());
        Assert.Equal("aGVsbG8=", result["data"]!.GetValue<string>());
        Assert.Equal(5, result["byte_length"]!.GetValue<int>());
        Assert.Contains($"GET /agent/v1/runs/{runId}/payloads/{payloadId}", seen);
    }

    [Fact]
    public async Task Run_trust_approves_a_pending_challenge()
    {
        var runId = "run_" + new string('a', 32);
        string? postBody = null;
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postBody = await request.Content!.ReadAsStringAsync();
                return RpcTestHost.Json($"{{\"run_id\":\"{runId}\",\"trust_request_id\":\"tr_1\",\"status\":\"running\"}}");
            }

            return RpcTestHost.Json($"{{\"run_id\":\"{runId}\",\"session_id\":\"s1\",\"workspace_id\":\"w1\",\"status\":\"waiting_workspace_trust\",\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1,\"policy_snapshot_sha256\":\"x\",\"version\":2,\"last_sequence\":3,\"created_at\":\"2026-09-29T00:00:00Z\",\"updated_at\":\"2026-09-29T00:00:00Z\",\"terminal_at\":null,\"terminal_error_code\":null,\"trust_request_id\":\"tr_1\",\"display_identity_hash\":\"hash_1\"}}");
        }, seen);
        var result = await dispatcher.DispatchAsync(
            Req("run.trust", $"{{\"runId\":\"{runId}\",\"approve\":true}}"),
            CancellationToken.None);
        Assert.Contains($"GET /agent/v1/runs/{runId}", seen);
        Assert.Contains($"POST /agent/v1/runs/{runId}/trust", seen);
        Assert.Contains("\"decision\":\"approve\"", postBody);
        Assert.Equal(runId, result!["run_id"]!.GetValue<string>());
        Assert.Equal("running", result["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_trust_without_a_pending_challenge_fails()
    {
        var runId = "run_" + new string('a', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(
            $"{{\"run_id\":\"{runId}\",\"session_id\":\"s1\",\"workspace_id\":\"w1\",\"status\":\"running\",\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1,\"policy_snapshot_sha256\":\"x\",\"version\":2,\"last_sequence\":3,\"created_at\":\"2026-09-29T00:00:00Z\",\"updated_at\":\"2026-09-29T00:00:00Z\",\"terminal_at\":null,\"terminal_error_code\":null}}")), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Req("run.trust", $"{{\"runId\":\"{runId}\"}}"), CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("trust_failed", ex.RuCode);
        Assert.Contains($"GET /agent/v1/runs/{runId}", seen);
        Assert.DoesNotContain(seen, route => route.Contains("/trust", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("allow_once", "allow_once")]
    [InlineData("allow_session_directory", "allow_session_directory")]
    [InlineData("allow_always", "allow_always")]
    [InlineData("deny", "deny")]
    public async Task Run_decide_maps_the_scope_to_the_command_wire(string decision, string expected)
    {
        string? body = null;
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);
        var dispatcher = await CreateInitializedAsync(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return RpcTestHost.Json("{\"status\":\"ok\"}");
        });
        await dispatcher.DispatchAsync(
            Req("run.approve", $"{{\"runId\":\"{runId}\",\"approvalId\":\"{approvalId}\",\"decision\":\"{decision}\"}}"),
            CancellationToken.None);
        Assert.Contains($"\"command\":\"{expected}\"", body);
    }

    [Fact]
    public async Task Run_cancel_posts_the_cancel_route()
    {
        var runId = "run_" + new string('a', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\"}")), seen);
        await dispatcher.DispatchAsync(Req("run.cancel", $"{{\"runId\":\"{runId}\"}}"), CancellationToken.None);
        Assert.Contains($"POST /agent/v1/runs/{runId}/cancel", seen);
    }

    [Fact]
    public async Task Run_reconcile_requires_outcome_and_hash()
    {
        var runId = "run_" + new string('a', 32);
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Req("run.reconcile", $"{{\"runId\":\"{runId}\"}}"), CancellationToken.None));
        Assert.Equal(RpcProtocol.InvalidParams, ex.RpcErrorCode);
    }

    [Fact]
    public async Task Run_attach_preflights_an_unknown_run()
    {
        var runId = "run_" + new string('a', 32);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(
            RpcTestHost.Json("{\"error_code\":\"run_not_found\",\"message\":\"unknown run\"}", HttpStatusCode.NotFound)), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(Req("run.attach", $"{{\"runId\":\"{runId}\"}}"), CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("run_not_found", ex.RuCode);
        Assert.Equal(404, ex.HttpStatus);
        Assert.Contains($"GET /agent/v1/runs/{runId}", seen);
    }

    [Fact]
    public async Task Run_output_rejects_payloads_over_the_frame_cap()
    {
        var runId = "run_" + new string('a', 32);
        var payloadId = "payload_" + new string('b', 32);
        var content = new string('a', 900_000);
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json(
            JsonSerializer.Serialize(new
            {
                run_id = runId,
                payload_id = payloadId,
                kind = "tool_output",
                encoding = "utf8text",
                content,
            }))));
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.output", $"{{\"runId\":\"{runId}\",\"payloadId\":\"{payloadId}\"}}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("output_frame_exceeds_limit", ex.RuCode);
        Assert.Contains("agent output", ex.Message);
    }

    [Fact]
    public async Task Run_reconcile_success_passes_the_body_through()
    {
        var runId = "run_" + new string('a', 32);
        var hash = new string('c', 64);
        string? body = null;
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return RpcTestHost.Json($"{{\"outcome\":\"applied\",\"snapshot_hash\":\"{hash}\"}}");
        }, seen);
        var result = await dispatcher.DispatchAsync(
            Req("run.reconcile", $"{{\"runId\":\"{runId}\",\"outcome\":\"applied\",\"hash\":\"{hash}\"}}"),
            CancellationToken.None);
        Assert.Contains($"POST /agent/v1/runs/{runId}/reconcile", seen);
        Assert.Contains("\"outcome\":\"applied\"", body);
        Assert.Contains($"\"snapshot_hash\":\"{hash}\"", body);
        Assert.Equal("applied", result!["outcome"]!.GetValue<string>());
        Assert.Equal(hash, result["snapshot_hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_reconcile_maps_the_host_error_code()
    {
        var runId = "run_" + new string('a', 32);
        var hash = new string('c', 64);
        List<string> seen = [];
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(
            RpcTestHost.Json("{\"error_code\":\"reconcile_stale\",\"message\":\"stale snapshot\"}", HttpStatusCode.Conflict)), seen);
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.reconcile", $"{{\"runId\":\"{runId}\",\"outcome\":\"applied\",\"hash\":\"{hash}\"}}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("reconcile_stale", ex.RuCode);
        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal("stale snapshot", ex.Message);
    }

    [Fact]
    public async Task Run_reconcile_fails_bounded_on_an_oversized_response()
    {
        var runId = "run_" + new string('a', 32);
        var hash = new string('c', 64);
        var dispatcher = await CreateInitializedAsync(_ =>
        {
            var response = RpcTestHost.Json("{}");
            response.Content.Headers.ContentLength = BoundedHttpResponseReader.MaximumBytes + 1;
            return Task.FromResult(response);
        });
        var ex = await Assert.ThrowsAsync<RpcDispatchException>(
            () => dispatcher.DispatchAsync(
                Req("run.reconcile", $"{{\"runId\":\"{runId}\",\"outcome\":\"applied\",\"hash\":\"{hash}\"}}"),
                CancellationToken.None));
        Assert.Equal(RpcProtocol.ApplicationError, ex.RpcErrorCode);
        Assert.Equal("response_too_large", ex.RuCode);
        Assert.True(ex.Message.Length <= 200);
    }

    [Fact]
    public void MethodNames_matches_the_documented_v1_surface()
    {
        Assert.Equal(
            [
                "initialize",
                "host.status",
                "session.create",
                "session.list",
                "session.rewind",
                "run.start",
                "run.attach",
                "run.detach",
                "run.trust",
                "run.approve",
                "run.cancel",
                "run.continue",
                "run.status",
                "run.command",
                "run.reconcile",
                "run.output",
                "policy.list",
                "policy.import",
                "policy.remove",
            ],
            RpcDispatcher.MethodNames);
        Assert.Equal(["run.event", "run.transient", "run.detached", "bridge.shutdown"], RpcDispatcher.NotificationNames);
    }
}
