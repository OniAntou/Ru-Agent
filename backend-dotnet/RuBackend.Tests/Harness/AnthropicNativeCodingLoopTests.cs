using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HeadlessCodingLoopTests
{    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_anthropic_tools_execute_reads_and_writes_then_resume_fresh_approval(bool keepBrokerProcess)
    {
        using var workspace = GitWorkspace.Create();
        using var approvalFile = ApprovalFile.Create();
        var settings = new SettingsStore(workspace.DataPaths);
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "Anthropic",
            AnthropicModel = "claude-fable-5",
        }, default);
        var credentials = new EphemeralTestCredentialStore();
        await credentials.SetAsync("Ru", "anthropic_api_key", "synthetic-test-key", default);
        var provider = new ScriptedAnthropicHandler(approvalFile.Path);
        using var broker = keepBrokerProcess ? SharedBrokerSession.Create() : null;
        string runId;
        string approvalId;
        using (var firstFactory = new BackendWebApplicationFactory(
                   dataPaths: workspace.DataPaths,
                   credentialStore: credentials,
                   settingsStore: settings,
                   withInProcessBroker: true,
                   providerHandler: provider,
                   sharedBrokerDescriptorPath: broker?.DescriptorPath))
        {
            using var client = firstFactory.CreateAuthenticatedClient();
            runId = await CreateTrustedRunAsync(client, workspace.Path);
            var pinned = await firstFactory.Services.GetRequiredService<IAgentRunModelMetadataResolver>()
                .ResolveAsync(new RunId(runId), CancellationToken.None);
            Assert.Equal("anthropic_messages", pinned.Protocol);
            Assert.Equal("claude-fable-5", pinned.ModelLabel);
            Assert.Equal(ProviderToolCallingMode.Native, pinned.ToolCallingMode);
            Assert.True(await WaitForStatusAsync(client, runId,
                    new HashSet<string>(StringComparer.Ordinal) { "waiting_approval", "completed", "failed" }, TimeSpan.FromSeconds(30)),
                "Anthropic native run did not reach a durable state");
            Assert.Equal("waiting_approval", await GetStatusAsync(client, runId));
            Assert.Equal(3, provider.Requests.Count);
            var events = await ReadEventsAsync(client, runId);
            Assert.Equal(5, events.Count(item => item.GetProperty("event_type").GetString() == "tool.execution_completed"));
            approvalId = events.Last(item => item.GetProperty("event_type").GetString() == "approval.requested")
                .GetProperty("approval_id").GetString()!;
            Assert.Equal("third", File.ReadAllText(System.IO.Path.Combine(workspace.Path, "native-sequence.txt")));
            Assert.Equal("offline approval read fixture", File.ReadAllText(approvalFile.Path));

            var request = provider.Requests[0];
            Assert.Equal("read", request["tools"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(System.Text.Json.JsonValueKind.Object, request["tools"]![0]!["input_schema"]!.GetValueKind());
            AssertNativeResults(provider.Requests[1], new HashSet<string>(StringComparer.Ordinal) { "toolu-read-b" }, "toolu-read-a", "toolu-read-b");
            AssertNativeResults(provider.Requests[2], new HashSet<string>(StringComparer.Ordinal), "toolu-write-a", "toolu-write-b", "toolu-write-c");
            Assert.Contains(events, item => item.GetProperty("event_type").GetString() == "approval.requested");
            AssertNoNativeSecrets(events);
            var payloadStore = firstFactory.Services.GetRequiredService<HarnessPayloadStore>();
            var payloadIds = await payloadStore.ListPayloadIdsAsync(new RunId(runId), HarnessPayloadKind.ProviderContinuation, CancellationToken.None);
            Assert.Equal(3, payloadIds.Count);
            foreach (var payloadId in payloadIds)
            {
                Assert.Equal("payload_client_forbidden", (await Assert.ThrowsAsync<HarnessStoreException>(() =>
                    payloadStore.ReadForClientAsync(new RunId(runId), payloadId, CancellationToken.None))).Code);
                Assert.Equal("payload_model_forbidden", (await Assert.ThrowsAsync<HarnessStoreException>(() =>
                    payloadStore.ReadForModelAsync(new RunId(runId), payloadId, CancellationToken.None))).Code);
            }
        }

        using var restartedFactory = new BackendWebApplicationFactory(
            dataPaths: workspace.DataPaths,
            credentialStore: credentials,
            settingsStore: settings,
            withInProcessBroker: true,
            providerHandler: provider,
            sharedBrokerDescriptorPath: broker?.DescriptorPath);
        using var restartedClient = restartedFactory.CreateAuthenticatedClient();
        Assert.True(await WaitForApprovalCountAsync(restartedClient, runId, 2, TimeSpan.FromSeconds(30)));
        var rehydratedEvents = await ReadEventsAsync(restartedClient, runId);
        var refreshedId = rehydratedEvents.Last(item => item.GetProperty("event_type").GetString() == "approval.requested")
            .GetProperty("approval_id").GetString()!;
        Assert.NotEqual(approvalId, refreshedId);
        using var stale = await restartedClient.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_once" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var approve = await restartedClient.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{refreshedId}", new { command = "allow_once" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.True(await WaitForStatusAsync(restartedClient, runId,
            new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(30)));
        Assert.Equal("completed", await GetStatusAsync(restartedClient, runId));
        Assert.Equal("approved native edit", File.ReadAllText(approvalFile.Path));
        Assert.Equal(4, provider.Requests.Count);
        AssertNativeResults(provider.Requests[3], new HashSet<string>(StringComparer.Ordinal), "toolu-approval");
        var completed = await ReadEventsAsync(restartedClient, runId);
        Assert.Equal(6, completed.Count(item => item.GetProperty("event_type").GetString() == "tool.execution_completed"));
        AssertNoNativeSecrets(completed);
    }

    [Fact]
    public async Task Native_anthropic_interrupted_tool_stream_does_not_dispatch_or_persist_completion()
    {
        using var workspace = GitWorkspace.Create();
        var settings = new SettingsStore(workspace.DataPaths);
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        { Backend = "Anthropic", AnthropicModel = "claude-fable-5" }, default);
        var credentials = new EphemeralTestCredentialStore();
        await credentials.SetAsync("Ru", "anthropic_api_key", "synthetic-test-key", default);
        var provider = new InterruptedAnthropicHandler();
        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths,
            credentialStore: credentials, settingsStore: settings, withInProcessBroker: true, providerHandler: provider);
        using var client = factory.CreateAuthenticatedClient();

        var runId = await CreateTrustedRunAsync(client, workspace.Path);
        Assert.True(await WaitForStatusAsync(client, runId,
            new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(30)));

        Assert.Equal("failed", await GetStatusAsync(client, runId));
        Assert.Equal(3, provider.RequestCount);
        Assert.All(provider.Requests, call =>
        {
            Assert.Equal("claude-fable-5", call.Model);
            Assert.Equal("https://api.anthropic.com/v1/messages", call.Endpoint);
        });
        using (var runResponse = await client.GetAsync($"/agent/v1/runs/{runId}"))
        {
            var run = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("protocol_failure", run.GetProperty("terminal_error_code").GetString());
        }
        Assert.False(File.Exists(System.IO.Path.Combine(workspace.Path, "native-partial.txt")));
        var events = await ReadEventsAsync(client, runId);
        Assert.DoesNotContain(events, item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
        var continuations = await factory.Services.GetRequiredService<HarnessPayloadStore>().ListPayloadIdsAsync(
            new RunId(runId), HarnessPayloadKind.ProviderContinuation, CancellationToken.None);
        Assert.Empty(continuations);
    }

    private static void AssertNativeResults(JsonObject request, IReadOnlySet<string> errorCallIds, params string[] callIds)
    {
        var messages = request["messages"]!.AsArray();
        var assistant = messages.Last(message => message?["role"]?.GetValue<string>() == "assistant");
        var blocks = assistant!["content"]!.AsArray();
        Assert.Equal(callIds, blocks.Where(block => block?["type"]?.GetValue<string>() == "tool_use")
            .Select(block => block!["id"]!.GetValue<string>()));
        Assert.Contains(blocks, block => block?["type"]?.GetValue<string>() == "thinking"
            && block["signature"]?.GetValue<string>() == "anthropic-offline-signature");
        var resultMessage = messages.Last(message => message?["role"]?.GetValue<string>() == "user"
            && message["content"] is JsonArray content
            && content.Any(block => block?["type"]?.GetValue<string>() == "tool_result"));
        var resultBlocks = resultMessage!["content"]!.AsArray().ToArray();
        Assert.Equal(callIds, resultBlocks.Select(block => block!["tool_use_id"]!.GetValue<string>()));
        for (var index = 0; index < resultBlocks.Length; index++)
            Assert.Equal(errorCallIds.Contains(callIds[index]), resultBlocks[index]!["is_error"]!.GetValue<bool>());
    }

    private static void AssertNoNativeSecrets(IEnumerable<JsonElement> events)
    {
        var serialized = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("anthropic-private-thought", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic-offline-signature", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic-redacted-data", serialized, StringComparison.Ordinal);
    }

    private sealed class ScriptedAnthropicHandler(string approvalWritePath) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<JsonObject> _requests = new();

        public IReadOnlyList<JsonObject> Requests
        {
            get { lock (_gate) return _requests.Select(request => (JsonObject)request.DeepClone()).ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            int index;
            lock (_gate)
            {
                index = _requests.Count;
                _requests.Add((JsonObject)body.DeepClone());
            }
            var lifecycle = index switch
            {
                0 => ToolBatch(("toolu-read-a", "read", "{\"path\":\"seed.txt\"}"),
                    ("toolu-read-b", "read", "{\"path\":\"missing-native-file.txt\"}")),
                1 => ToolBatch(("toolu-write-a", "write", "{\"op\":\"write\",\"path\":\"native-sequence.txt\",\"content\":\"first\"}"),
                    ("toolu-write-b", "edit", "{\"path\":\"native-sequence.txt\",\"old_string\":\"first\",\"new_string\":\"second\"}"),
                    ("toolu-write-c", "edit", "{\"path\":\"native-sequence.txt\",\"old_string\":\"second\",\"new_string\":\"third\"}")),
                2 => ToolBatch(("toolu-approval", "write", JsonSerializer.Serialize(new
                { op = "write", path = approvalWritePath, content = "approved native edit", overwrite = true }))),
                _ => TextCompletion("Anthropic native workflow complete."),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string ToolBatch(params (string Id, string Name, string Arguments)[] calls)
        {
            var output = new StringBuilder();
            output.Append(Event("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject { ["model"] = "claude-fable-5", ["content"] = new JsonArray() },
            }));
            var index = 0;
            output.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "Working through the requested files." },
            }));
            output.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }));
            output.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
            }));
            output.Append(Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "anthropic-private-thought" },
            }));
            output.Append(Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "anthropic-offline-signature" },
            }));
            output.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }));
            output.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "redacted_thinking", ["data"] = "anthropic-redacted-data" },
            }));
            output.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }));
            foreach (var call in calls)
            {
                output.Append(Event("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start", ["index"] = index,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = new JsonObject(),
                    },
                }));
                output.Append(Event("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta", ["index"] = index,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = call.Arguments },
                }));
                output.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }));
            }
            output.Append(Event("message_delta", new JsonObject
            {
                ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" },
            }));
            output.Append(Event("message_stop", new JsonObject { ["type"] = "message_stop" }));
            return output.ToString();
        }

        private static string TextCompletion(string text) =>
            Event("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject { ["model"] = "claude-fable-5", ["content"] = new JsonArray() },
            })
            + Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
            })
            + Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text },
            })
            + Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 })
            + Event("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" } })
            + Event("message_stop", new JsonObject { ["type"] = "message_stop" });

        private static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";
    }

    private sealed class InterruptedAnthropicHandler : HttpMessageHandler
    {
        private readonly List<(string Model, string Endpoint)> _requests = new();
        private readonly object _gate = new();
        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        public IReadOnlyList<(string Model, string Endpoint)> Requests { get { lock (_gate) return _requests.ToArray(); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (_gate) _requests.Add((body["model"]!.GetValue<string>(), request.RequestUri!.AbsoluteUri));
            var stream = Event("message_start", new JsonObject
                {
                    ["type"] = "message_start",
                    ["message"] = new JsonObject { ["model"] = "claude-fable-5", ["content"] = new JsonArray() },
                })
                + Event("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start", ["index"] = 0,
                    ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu-interrupted", ["name"] = "write", ["input"] = new JsonObject() },
                })
                + Event("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta", ["index"] = 0,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"op\":\"write\",\"path\":\"native-partial.txt\",\"content\":\"must-not-run\"}" },
                })
                + Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";
    }


}
