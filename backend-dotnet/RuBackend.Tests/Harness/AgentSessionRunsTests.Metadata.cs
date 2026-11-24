using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentSessionRunsTests
{
    private static readonly string[] PinnedMetadataFields =
    [
        "provider_backend", "provider_model", "provider_protocol", "provider_model_label", "provider_calling_mode",
        "context_window_tokens", "context_window_source", "context_window_version", "output_token_reserve", "tool_profile",
    ];

    [Fact]
    public async Task Session_run_pages_preserve_each_completed_runs_provider_pin_after_settings_change()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionA = await CreateSessionAsync(client);
        var sessionB = await CreateSessionAsync(client);
        var selections = factory.Services.GetRequiredService<SqliteAgentRunProviderSelectionStore>();
        AgentRunProviderSelection[] pins =
        [
            MetadataPin("OpenAI", "openai", "openai_responses", "stored-openai", "Stored OpenAI", 128_000, 8192, AgentToolProfiles.Current),
            MetadataPin("Anthropic", "anthropic", "anthropic_messages", "stored-claude", "Stored Claude", 200_000, 4096, AgentToolProfiles.Minimal),
            MetadataPin("OpenCode Go", "opencode-go", "chat_completions", "stored-deepseek", "Stored DeepSeek", 65_536, 2048, AgentToolProfiles.Current),
        ];
        var expected = new Dictionary<string, AgentRunProviderSelection>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            var run = await CreateRunAsync(client, sessionA, tmp.Path, "metadata fixture", Guid.NewGuid().ToString("N"));
            await selections.PinAsync(new RunId(run), pin, default);
            await CompleteMetadataRunAsync(factory, run);
            expected.Add(run, pin);
        }
        var foreignPin = pins[0] with { Model = "foreign-session-model", ModelLabel = "Foreign session model" };
        var foreignRun = await CreateRunAsync(client, sessionB, tmp.Path, "other session", Guid.NewGuid().ToString("N"));
        await selections.PinAsync(new RunId(foreignRun), foreignPin, default);
        await CompleteMetadataRunAsync(factory, foreignRun);
        await ChangeMetadataDefaultsAsync(factory);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        string? firstCursor = null;
        var pages = 0;
        do
        {
            var suffix = cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor);
            using var response = await client.GetAsync($"/agent/v1/sessions/{sessionA}/runs?limit=2{suffix}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 2);
            foreach (var item in items)
            {
                var run = item.GetProperty("run_id").GetString()!;
                Assert.True(seen.Add(run), "A cursor must not return the same run twice.");
                Assert.Equal(sessionA, item.GetProperty("session_id").GetString());
                Assert.Equal("completed", item.GetProperty("status").GetString());
                Assert.NotEqual(foreignRun, run);
                AssertPinnedMetadata(item, expected[run]);
                AssertPrivateSelectionAbsent(item, expected[run]);

                using var individual = await client.GetAsync($"/agent/v1/runs/{run}");
                Assert.Equal(HttpStatusCode.OK, individual.StatusCode);
                var loaded = await individual.Content.ReadFromJsonAsync<JsonElement>();
                AssertPinnedMetadata(loaded, expected[run]);
                AssertPrivateSelectionAbsent(loaded, expected[run]);
                foreach (var field in PinnedMetadataFields)
                    Assert.Equal(loaded.GetProperty(field).GetRawText(), item.GetProperty(field).GetRawText());
            }
            cursor = page.GetProperty("next_cursor").GetString();
            firstCursor ??= cursor;
            pages++;
        } while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal(expected.Keys.OrderBy(value => value), seen.OrderBy(value => value));
        Assert.NotNull(firstCursor);
        using var foreignCursor = await client.GetAsync($"/agent/v1/sessions/{sessionB}/runs?limit=2&cursor={Uri.EscapeDataString(firstCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, foreignCursor.StatusCode);
        using var otherSession = await client.GetAsync($"/agent/v1/sessions/{sessionB}/runs?limit=2");
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
        var otherPage = await otherSession.Content.ReadFromJsonAsync<JsonElement>();
        var onlyForeign = Assert.Single(otherPage.GetProperty("items").EnumerateArray());
        Assert.Equal(foreignRun, onlyForeign.GetProperty("run_id").GetString());
        AssertPinnedMetadata(onlyForeign, foreignPin);
        AssertPrivateSelectionAbsent(onlyForeign, foreignPin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_runs_without_a_pin_do_not_borrow_current_global_defaults(bool missingSelectionRow)
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);
        var run = await CreateRunAsync(client, session, tmp.Path, "legacy metadata fixture", Guid.NewGuid().ToString("N"));
        await CompleteMetadataRunAsync(factory, run);
        if (missingSelectionRow)
        {
            var options = factory.Services.GetRequiredService<HarnessSqliteOptions>();
            await using var connection = await options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_run_provider_selections WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", run);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await ChangeMetadataDefaultsAsync(factory);

        using var listed = await client.GetAsync($"/agent/v1/sessions/{session}/runs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var listBody = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(listBody.GetProperty("items").EnumerateArray());
        Assert.Equal(run, item.GetProperty("run_id").GetString());
        using var individual = await client.GetAsync($"/agent/v1/runs/{run}");
        Assert.Equal(HttpStatusCode.OK, individual.StatusCode);
        var getBody = await individual.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in PinnedMetadataFields)
        {
            Assert.True(!item.TryGetProperty(field, out var listedValue) || listedValue.ValueKind == JsonValueKind.Null, field);
            Assert.True(!getBody.TryGetProperty(field, out var loadedValue) || loadedValue.ValueKind == JsonValueKind.Null, field);
        }
        Assert.DoesNotContain("global-model-must-not-replace-pins", listBody.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listed_anthropic_pin_supports_shared_client_cached_input_and_context_percentage()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var http = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(http);
        var run = await CreateRunAsync(http, session, tmp.Path, "cached input fixture", Guid.NewGuid().ToString("N"));
        var pin = MetadataPin("Anthropic", "anthropic", "anthropic_messages", "stored-claude", "Stored Claude", 100_000, 4096, AgentToolProfiles.Current);
        await factory.Services.GetRequiredService<SqliteAgentRunProviderSelectionStore>().PinAsync(new RunId(run), pin, default);
        var usage = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            input_tokens = 1000, cache_read_input_tokens = 19_000, cache_write_input_tokens = 5000,
            input_tokens_include_cache = false, output_tokens = 200, total_tokens = 25_200,
        }));
        var payload = await CompleteMetadataRunAsync(factory, run, usage);
        await ChangeMetadataDefaultsAsync(factory);
        var descriptor = new RuntimeDescriptor(RuntimeDescriptor.CurrentProtocolVersion, http.BaseAddress!.ToString(),
            "test-secret", Environment.ProcessId, "0", "metadata-test-instance", DateTimeOffset.UtcNow.ToString("O"));
        using var client = new AgentApiClient(new PublicHostSession(http.BaseAddress, "test-secret", descriptor), http);

        var summary = Assert.Single(await client.ListSessionRunsAsync(session));
        Assert.Equal("Anthropic", summary.ProviderBackend);
        Assert.Equal("stored-claude", summary.ProviderModel);
        Assert.Equal("anthropic_messages", summary.ProviderProtocol);
        Assert.Equal(100_000, summary.ContextWindowTokens);
        Assert.Equal(4096, summary.OutputTokenReserve);
        Assert.Equal(AgentToolProfiles.Current, summary.ToolProfile);
        var storedUsage = await client.GetPayloadContentAsync(run, payload!);
        Assert.Equal("modelusage", storedUsage.Kind);
        Assert.True(ProviderPromptTokens.TryRead(storedUsage.Content, summary.ProviderProtocol, out var promptTokens, out _));
        Assert.Equal(25_000, promptTokens);
        Assert.Equal(25d, 100d * promptTokens!.Value / summary.ContextWindowTokens!.Value);
    }

    private static AgentRunProviderSelection MetadataPin(string backend, string alias, string protocol, string model,
        string label, long contextWindow, int reserve, string toolProfile) => new(
            backend, alias, protocol, $"https://{alias}.metadata-private.invalid/v1", model, alias + "_api_key",
            JsonSerializer.Serialize(new { ru_tool_profile = toolProfile, private_selection_marker = "private-options-" + alias }),
            1024, label, "native", contextWindow, "provider_catalog", "metadata-test-v1", reserve);

    private static void AssertPinnedMetadata(JsonElement item, AgentRunProviderSelection pin)
    {
        Assert.Equal(pin.Backend, item.GetProperty("provider_backend").GetString());
        Assert.Equal(pin.Model, item.GetProperty("provider_model").GetString());
        Assert.Equal(pin.Protocol, item.GetProperty("provider_protocol").GetString());
        Assert.Equal(pin.ModelLabel, item.GetProperty("provider_model_label").GetString());
        Assert.Equal(pin.CallingMode, item.GetProperty("provider_calling_mode").GetString());
        Assert.Equal(pin.ContextWindowTokens, item.GetProperty("context_window_tokens").GetInt64());
        Assert.Equal(pin.ContextWindowSource, item.GetProperty("context_window_source").GetString());
        Assert.Equal(pin.ContextWindowVersion, item.GetProperty("context_window_version").GetString());
        Assert.Equal(pin.OutputTokenReserve, item.GetProperty("output_token_reserve").GetInt32());
        using var options = JsonDocument.Parse(pin.RequestOptionsJson);
        Assert.Equal(options.RootElement.GetProperty("ru_tool_profile").GetString(), item.GetProperty("tool_profile").GetString());
    }

    private static void AssertPrivateSelectionAbsent(JsonElement item, AgentRunProviderSelection pin)
    {
        foreach (var field in new[] { "account", "base_uri", "request_options_json", "maximum_output_tokens", "alias" })
            Assert.False(item.TryGetProperty(field, out _), field);
        var json = item.GetRawText();
        Assert.DoesNotContain(pin.BaseUri, json, StringComparison.Ordinal);
        Assert.DoesNotContain(pin.Account!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-options-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private_selection_marker", json, StringComparison.Ordinal);
    }

    private static async Task ChangeMetadataDefaultsAsync(BackendWebApplicationFactory factory)
    {
        var settings = factory.Services.GetRequiredService<ISettingsStore>();
        var current = await settings.ReadAsync(default);
        await settings.SaveAsync(current with
        {
            Backend = "Custom OpenAI", CustomModel = "global-model-must-not-replace-pins",
            CustomUrl = "https://global-default.metadata-private.invalid/v1",
        }, default);
    }

    private static async Task<string?> CompleteMetadataRunAsync(BackendWebApplicationFactory factory, string runId, byte[]? usage = null)
    {
        // These read-contract fixtures terminate through the real durable store without
        // granting workspace trust or invoking any provider/execution authority.
        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(new RunId(runId), default);
        Assert.NotNull(run);
        var now = DateTimeOffset.UtcNow;
        var events = new List<HarnessEventIntent>();
        var payloads = new List<HarnessPayloadIntent>();
        PayloadId? payloadId = null;
        if (usage is not null)
        {
            payloadId = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
            payloads.Add(new HarnessPayloadIntent(payloadId.Value, HarnessPayloadKind.ModelUsage,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, usage.Length, usage));
            events.Add(new HarnessEventIntent(new EventId("evt_" + Guid.NewGuid().ToString("N")),
                HarnessEventKind.ModelUsageRecorded, payloadId: payloadId));
        }
        events.Add(new HarnessEventIntent(new EventId("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged));
        var completed = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, new(HarnessRunState.Completed),
            run.Version + 1, run.LastSequence + events.Count, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
            run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, now, terminalAt: now,
            permissionProfileVersion: run.PermissionProfileVersion);
        var result = await store.CommitAsync(new HarnessCommit(run.RunId, run.Version, completed, events, payloads, []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        return payloadId?.Value;
    }
}
