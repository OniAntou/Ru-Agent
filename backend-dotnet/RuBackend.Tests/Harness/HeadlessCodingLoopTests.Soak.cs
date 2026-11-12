using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business.Settings;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Harness;

// Long-run soak over the REAL host (SQLite stores, coordinator, compaction, in-process model
// runner, continuation replay) against a fake DeepSeek-style provider that streams a large
// reasoning_content and one read tool call per step. Skipped unless RU_SOAK_STEPS is set, so it
// never runs in the normal suite. Prints one SOAK line per finding; asserts nothing about the
// outcome, because its job is to find where a long run stops.
public sealed partial class HeadlessCodingLoopTests
{
    [Fact]
    public async Task Soak_long_run_with_replayed_reasoning()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("RU_SOAK_STEPS"), out var steps) || steps <= 0) return;
        var reasoningKb = int.TryParse(Environment.GetEnvironmentVariable("RU_SOAK_REASONING_KB"), out var kb) ? kb : 50;
        var fileKb = int.TryParse(Environment.GetEnvironmentVariable("RU_SOAK_FILE_KB"), out var fkb) ? fkb : 6;

        using var workspace = GitWorkspace.Create();
        for (var i = 0; i < steps; i++)
        {
            var text = new StringBuilder();
            for (var line = 0; text.Length < fileKb * 1024; line++)
                text.Append($"file {i} line {line}: the quick brown fox {i * 7919 + line} jumps over the lazy dog\n");
            await File.WriteAllTextAsync(System.IO.Path.Combine(workspace.Path, $"soak-{i}.txt"), text.ToString());
        }

        var settings = new SettingsStore(workspace.DataPaths);
        await settings.SaveAsync((await settings.ReadAsync(default)) with
        {
            Backend = "Custom OpenAI",
            CustomUrl = "https://api.deepseek.com/v1",
            CustomModel = "deepseek-v4-flash",
        }, default);
        var credentials = new EphemeralTestCredentialStore();
        await credentials.SetAsync("Ru", "custom_api_key", "synthetic-test-key", default);
        var mixed = Environment.GetEnvironmentVariable("RU_SOAK_MIX") == "1";
        if (mixed) Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, "out"));
        var provider = new SoakProviderHandler(steps, reasoningKb * 1024, mixed);

        using var factory = new BackendWebApplicationFactory(
            dataPaths: workspace.DataPaths, credentialStore: credentials, settingsStore: settings, providerHandler: provider,
            configureServices: services => services.AddHttpClient("ProviderCatalog")
                .ConfigurePrimaryHttpMessageHandler(() => new SoakCatalogHandler()));
        using var client = factory.CreateAuthenticatedClient();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("RU_SOAK_MINUTES"), out var m) && m > 0 ? m : 8;
        var finished = await WaitForStatusAsync(client, runId,
            new HashSet<string>(StringComparer.Ordinal) { "completed", "failed", "waiting_approval", "cancelled" },
            TimeSpan.FromMinutes(minutes));
        clock.Stop();

        var status = await GetStatusAsync(client, runId);
        var events = await ReadEventsAsync(client, runId);
        string Kind(JsonElement e) => e.GetProperty("event_type").GetString() ?? "";
        var failed = events.LastOrDefault(e => Kind(e) == "run.failed");
        var kinds = events.GroupBy(Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine($"SOAK status={status} reachedTerminalInTime={finished} elapsed={clock.Elapsed.TotalSeconds:F1}s " +
            $"providerRequests={provider.Samples.Count} plannedSteps={steps} reasoningKb={reasoningKb} fileKb={fileKb}");
        Console.WriteLine("SOAK failure=" + (failed.ValueKind == JsonValueKind.Undefined
            ? "none"
            : failed.TryGetProperty("safe_summary", out var summary) ? summary.GetString() : failed.ToString()));
        Console.WriteLine("SOAK events: " + string.Join(' ', kinds));
        var stride = provider.Samples.Count <= 130 ? 1 : Math.Max(1, provider.Samples.Count / 14);
        for (var i = 0; i < provider.Samples.Count; i += stride)
        {
            var s = provider.Samples[i];
            Console.WriteLine($"SOAK request#{i,3} messages={s.Messages,4} bodyKb={s.BodyBytes / 1024,6} reasoningKbInRequest={s.ReplayedReasoningBytes / 1024,6} tools={s.HasTools} checkpoint={s.IsCheckpoint}");
        }

        foreach (var e in events.TakeLast(14))
            Console.WriteLine($"SOAK tail seq={e.GetProperty("sequence").GetInt64()} {Kind(e)} " +
                (e.TryGetProperty("safe_summary", out var ss) ? ss.GetString() : ""));
        if (provider.Samples.Count > 0)
        {
            var last = provider.Samples[^1];
            Console.WriteLine($"SOAK checkpointRequests={provider.Samples.Count(s => s.IsCheckpoint)}");
            Console.WriteLine($"SOAK last request#{provider.Samples.Count - 1} messages={last.Messages} bodyKb={last.BodyBytes / 1024} reasoningKbInRequest={last.ReplayedReasoningBytes / 1024}");
        }

        // RU_SOAK_ASSERT=1 turns the probe into a gate: the run must finish, and no request may
        // reach the runner's history window, where the oldest history is dropped without a
        // checkpoint (the original request with it).
        if (Environment.GetEnvironmentVariable("RU_SOAK_ASSERT") == "1")
        {
            Assert.Equal("completed", status);
            var windowLimit = RuAgent.Runtime.Model.ModelGenerationBounds.MaxModelMessages;
            Assert.All(provider.Samples, sample => Assert.True(sample.Messages < windowLimit,
                $"a request carried {sample.Messages} messages, at the {windowLimit}-message window"));

            // A reopened long run shows what it was doing last, not only its first rows.
            var replay = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}/activities");
            var replayed = replay.GetProperty("activities").EnumerateArray()
                .Select(row => row.TryGetProperty("durable_sequence", out var sequence) && sequence.ValueKind == JsonValueKind.Number
                    ? sequence.GetInt64() : 0L)
                .ToArray();
            var lastToolSequence = events.Last(e => Kind(e) == "tool.execution_completed").GetProperty("sequence").GetInt64();
            Assert.Contains(replayed, sequence => sequence >= lastToolSequence - 8);

            // Mixed mode: every write landed, and every tool call (writes and terminal
            // launches included) completed rather than failing or asking.
            if (mixed)
            {
                var writes = Enumerable.Range(0, steps).Count(step => step % 3 == 1);
                Assert.Equal(writes, Directory.GetFiles(System.IO.Path.Combine(workspace.Path, "out"), "out-*.txt").Length);
                Assert.DoesNotContain(events, e => Kind(e) is "approval.requested" or "tool.execution_failed");
                var completed = events.Where(e => Kind(e) == "tool.execution_completed").ToArray();
                Assert.Equal(steps, completed.Length);
                Assert.All(completed, e => Assert.Equal("ok", e.GetProperty("safe_summary").GetString()));
            }
        }
    }

    /// <summary>The model catalog: the one model, with the real DeepSeek context window.</summary>
    private sealed class SoakCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"deepseek-v4-flash","context_length":1000000}]}""", Encoding.UTF8, "application/json"),
            });
    }

    private sealed record SoakSample(int Messages, long BodyBytes, long ReplayedReasoningBytes, bool HasTools, bool IsCheckpoint);

    private sealed class SoakProviderHandler(int steps, int reasoningBytes, bool mixed = false) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<SoakSample> _samples = new();
        private int _toolSteps;

        public IReadOnlyList<SoakSample> Samples
        {
            get { lock (_gate) return _samples.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                // The model catalog: a context window big enough to be the real DeepSeek one.
                const string models = """{"data":[{"id":"deepseek-v4-flash","context_length":1000000}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(models, Encoding.UTF8, "application/json") };
            }

            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonNode.Parse(raw)!.AsObject();
            var messages = body["messages"] as JsonArray ?? new JsonArray();
            long replayed = 0;
            foreach (var message in messages)
                if (message is JsonObject o && o["reasoning_content"] is JsonValue v && v.TryGetValue<string>(out var text))
                    replayed += Encoding.UTF8.GetByteCount(text);
            var hasTools = body["tools"] is JsonArray { Count: > 0 };
            // Compaction asks the model for a checkpoint: a fixed Markdown outline, no tool calls.
            var isCheckpoint = raw.Contains("Produce a compact checkpoint of the preceding history", StringComparison.Ordinal)
                || raw.Contains("untrusted candidate was rejected for checkpoint format", StringComparison.Ordinal);

            int step;
            lock (_gate)
            {
                _samples.Add(new SoakSample(messages.Count, Encoding.UTF8.GetByteCount(raw), replayed, hasTools, isCheckpoint));
                step = hasTools && !isCheckpoint ? _toolSteps++ : -1;
            }

            string sse;
            if (isCheckpoint)
            {
                string[] outline =
                [
                    "## Request and Intent", "- read every soak file",
                    "## Decisions and Constraints", "- read only",
                    "## Files and Changes", "- none changed",
                    "## Results and Failures", "- reads succeeded",
                    "## Pending Work", "- keep reading",
                    "## Current Work", "- reading soak files",
                    "## Next Step", "- read the next file",
                    "## Critical Context", "- none",
                ];
                var summaryChunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject
                {
                    ["delta"] = new JsonObject { ["content"] = string.Join(Environment.NewLine, outline) },
                    ["finish_reason"] = "stop",
                }) };
                sse = Chunk(summaryChunk.ToJsonString());
            }
            else if (step < steps)
            {
                var filler = new StringBuilder();
                for (var i = 0; filler.Length < reasoningBytes; i++)
                    filler.Append($"step {step} considering file {i}: weigh the options, compare the results, plan the next read. ");
                // Real providers stream reasoning in small deltas, not one 50 KB line.
                var reasoningText = filler.ToString();
                var reasoningSse = new StringBuilder();
                for (var offset = 0; offset < reasoningText.Length; offset += 400)
                {
                    var piece = reasoningText.Substring(offset, Math.Min(400, reasoningText.Length - offset));
                    var reasoningChunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject
                    {
                        ["delta"] = new JsonObject { ["reasoning_content"] = piece },
                    }) };
                    reasoningSse.Append(Chunk(reasoningChunk.ToJsonString()));
                }

                var (toolName, toolArguments) = SoakCall(step, mixed);
                var toolChunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject
                {
                    ["delta"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["id"] = $"call_soak_{step}",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = toolName,
                                ["arguments"] = toolArguments,
                            },
                        }),
                    },
                    ["finish_reason"] = "tool_calls",
                }) };
                sse = reasoningSse + Chunk(toolChunk.ToJsonString());
            }
            else
            {
                sse = Chunk("""{"choices":[{"delta":{"reasoning_content":"all files read"}}]}""")
                    + Chunk("""{"choices":[{"delta":{"content":"All files were read; nothing needed changing."},"finish_reason":"stop"}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string Chunk(string json) => "data: " + json + "\n\n";

        /// <summary>
        /// The tool call of one soak step. Mixed mode (RU_SOAK_MIX=1) rotates a read, a file
        /// write and a PowerShell command, so a long run also exercises mutations, the file
        /// journal and terminal launches, not only reads.
        /// </summary>
        internal static (string Name, string Arguments) SoakCall(int step, bool mixed) => !mixed
            ? ("read", JsonSerializer.Serialize(new { path = $"soak-{step}.txt" }))
            : (step % 3) switch
            {
                0 => ("read", JsonSerializer.Serialize(new { path = $"soak-{step}.txt" })),
                1 => ("write_file", JsonSerializer.Serialize(new { path = $"out/out-{step}.txt", content = $"step {step} wrote this\n" })),
                _ => ("powershell", JsonSerializer.Serialize(new
                {
                    script = $"(Get-Content -LiteralPath 'out/out-{step - 1}.txt').Length",
                    purpose = "inspection",
                })),
            };
    }
}
