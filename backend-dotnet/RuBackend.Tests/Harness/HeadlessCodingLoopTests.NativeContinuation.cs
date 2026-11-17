using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Business.Settings;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>Native continuation on the Responses protocol, end to end (ADR 0049).</summary>
public sealed partial class HeadlessCodingLoopTests
{
    [Fact]
    public async Task A_new_message_under_the_same_responses_model_replays_the_previous_run_natively()
    {
        using var workspace = GitWorkspace.Create();
        var settings = new SettingsStore(workspace.DataPaths);
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "OpenAI",
            OpenAiModel = "gpt-5.6-luna",
        }, default);
        var credentials = new EphemeralTestCredentialStore();
        await credentials.SetAsync("Ru", "openai_api_key", "synthetic-test-key", default);
        var provider = new ContinuedResponsesHandler();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: workspace.DataPaths,
            credentialStore: credentials,
            settingsStore: settings,
            providerHandler: provider);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var terminal = new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" };

        var first = await StartRunInSessionAsync(client, sessionId, workspace.Path, "read the seed");
        Assert.True(await WaitForStatusAsync(client, first, terminal, TimeSpan.FromSeconds(30)), "first run did not end");
        Assert.Equal("completed", await GetStatusAsync(client, first));
        var second = await StartRunInSessionAsync(client, sessionId, workspace.Path, "now continue");
        Assert.True(await WaitForStatusAsync(client, second, terminal, TimeSpan.FromSeconds(30)), "second run did not end");
        Assert.True(await GetStatusAsync(client, second) == "completed",
            JsonSerializer.Serialize(await ReadEventsAsync(client, second)));

        var requests = provider.Requests;
        Assert.Equal(3, requests.Count);
        // The second run's first request carries the first run's native items: its recorded
        // function call, the call's output and the encrypted reasoning, not a quoted handoff.
        var input = requests[2]["input"]!.AsArray();
        Assert.Contains(input, item => item?["type"]?.GetValue<string>() == "function_call"
            && item["call_id"]?.GetValue<string>() == "call-read-a");
        Assert.Contains(input, item => item?["type"]?.GetValue<string>() == "function_call_output"
            && item["call_id"]?.GetValue<string>() == "call-read-a");
        Assert.Contains(input, item => item?["type"]?.GetValue<string>() == "reasoning"
            && item["encrypted_content"]?.GetValue<string>() == "opaque-test-signature");
        Assert.DoesNotContain("session_run_handoff", requests[2].ToJsonString(), StringComparison.Ordinal);
    }

    private static async Task<string> StartRunInSessionAsync(HttpClient client, string sessionId, string workspacePath,
        string prompt)
    {
        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N"),
        });
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("run_id").GetString()!;
        // Workspace trust is asked once per identity; a later run in the same workspace skips it.
        if (created.TryGetProperty("trust_request_id", out var trustId) && trustId.ValueKind == JsonValueKind.String)
        {
            using var trust = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", new
            {
                trust_request_id = trustId.GetString(),
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
            trust.EnsureSuccessStatusCode();
        }
        return runId;
    }

    /// <summary>One read batch and a reply for the first run, then a reply for the second.</summary>
    private sealed class ContinuedResponsesHandler : HttpMessageHandler
    {
        private readonly List<JsonObject> _requests = new();
        private readonly object _gate = new();

        public IReadOnlyList<JsonObject> Requests
        {
            get { lock (_gate) return _requests.Select(request => (JsonObject)request.DeepClone()).ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            int index;
            lock (_gate)
            {
                index = _requests.Count;
                _requests.Add((JsonObject)body.DeepClone());
            }
            var events = index switch
            {
                0 => ScriptedResponsesHandler.NativeBatch("call-read-a", "item-read-a", "read", "{\"path\":\"seed.txt\"}"),
                1 => ScriptedResponsesHandler.TextCompletion("read the seed"),
                _ => ScriptedResponsesHandler.TextCompletion("continued natively"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
