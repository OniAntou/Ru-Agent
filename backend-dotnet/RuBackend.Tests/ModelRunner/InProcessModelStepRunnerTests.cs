using System.Net;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness;
using RuBackend.Harness.ModelRunner;
using RuBackend.Providers;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.ModelRunner;

public sealed partial class InProcessModelStepRunnerTests
{
    [Fact]
    public async Task Maps_deltas_and_returns_exactly_one_final_step()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta("Hello ", [], completed: false, finishReason: null),
            new ModelDelta(
                "world", [], completed: true, finishReason: "stop",
                usage: new ModelUsage(12, 5, 17, null, null))));
        var deltas = new List<AssistantDelta>();

        var result = await Runner(provider).RunStepAsync(
            Request(),
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("Hello world", result.Step.Text);
        Assert.Empty(result.Step.ToolCalls);
        Assert.Equal(AssistantFinishReason.Stop, result.Step.FinishReason);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(17, result.Usage.TotalTokens);
        Assert.Equal(["Hello ", "world"], deltas.Select(delta => delta.Content));
        Assert.Equal([1L, 2L], deltas.Select(delta => delta.Sequence));
        Assert.All(deltas, delta => Assert.False(delta.IsReset));
    }

    [Fact]
    public async Task A_usage_report_keeps_its_model_name_through_the_step()
    {
        // The report is written into a stored payload and looked up in a price
        // table; the name has to survive the in-process hop instead of being
        // flattened into the five counts.
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta(
                "done", [], completed: true, finishReason: "stop",
                usage: new ModelUsage(12, 5, 17, null, null, "vendor/paid"))));

        var result = await Runner(provider).RunStepAsync(
            Request(),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal("vendor/paid", result.Usage!.Model);
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Maps_provider_tool_calls_with_their_arguments()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta(
                null,
                [new ModelToolFragment("call-1", "read", "{\"path\":\"README.md\"}")],
                completed: true,
                finishReason: "tool_calls")));

        var result = await Runner(provider).RunStepAsync(
            Request(ContextWithReadCapability()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(AssistantFinishReason.ToolCalls, result.Step.FinishReason);
        var call = Assert.Single(result.Step.ToolCalls);
        Assert.Equal("call-1", call.ProviderCallId);
        Assert.Equal("read", call.ToolId);
        Assert.Equal("1.0.0", call.ToolVersion);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Content_filter_finish_maps_to_harness_without_parsing_partial_calls()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta(
                "filtered text",
                [new ModelToolFragment("call-1", "read", "{\"path\":")],
                completed: true,
                finishReason: "content_filter",
                usage: new ModelUsage(12, 5, 17, null, null))));

        var result = await Runner(provider).RunStepAsync(
            Request(ContextWithReadCapability()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(AssistantFinishReason.ContentFilter, result.Step.FinishReason);
        Assert.Empty(result.Step.ToolCalls);
        Assert.Equal(12, result.Usage!.InputTokens);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("auth_failed", false)]
    [InlineData("forbidden", null)]
    public async Task A_step_reports_what_it_learned_about_the_runs_key(string? failure, bool? observed)
    {
        var provider = new ScriptedProvider((_, _) => failure is null
            ? Deltas(new ModelDelta("ok", [], completed: true, finishReason: "stop"))
            : Throws(new ModelProviderException(failure, retryable: false)));
        var seen = new List<bool>();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
            ObserveProviderKey = (_, sentWith, accepted) =>
            {
                Assert.Equal(Bootstrap().Credential, sentWith.Credential);
                seen.Add(accepted);
            },
        };

        try { await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, default); }
        catch (ModelRunnerProtocolException) when (failure is not null) { }

        // A 403 can be a model or region the key may not use, so it says nothing about the key.
        Assert.Equal(observed is { } value ? [value] : [], seen);
    }

    [Fact]
    public async Task Retries_retryable_provider_error_with_a_fresh_attempt()
    {
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? Throws(new ModelProviderException("rate_limited", retryable: true))
            : Deltas(new ModelDelta("recovered", [], completed: true, finishReason: "stop")));
        var deltas = new List<string>();

        var result = await Runner(provider).RunStepAsync(
            Request(),
            delta =>
            {
                deltas.Add(delta.Content);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("recovered", result.Step.Text);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(["recovered"], deltas);
    }

    [Fact]
    public async Task Streams_partial_text_live_and_resets_it_when_the_attempt_is_retried()
    {
        // The retry must not stack its answer on top of the failed attempt's
        // partial output: attempt 2 is preceded by a reset frame and continues
        // the sequence monotonically.
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? DeltasThenThrows(
                new ModelDelta("half an ans", [], completed: false, finishReason: null),
                new ModelProviderException("rate_limited", retryable: true))
            : Deltas(new ModelDelta("the full answer", [], completed: true, finishReason: "stop")));
        var deltas = new List<AssistantDelta>();

        var result = await Runner(provider).RunStepAsync(
            Request(),
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("the full answer", result.Step.Text);
        Assert.Equal(2, provider.Calls);

        // The failed attempt's text was streamed immediately, not withheld.
        Assert.Equal("half an ans", deltas[0].Content);
        Assert.False(deltas[0].IsReset);

        // The retry is announced by a reset, then carries the replacement answer.
        Assert.True(deltas[1].IsReset);
        Assert.Equal(string.Empty, deltas[1].Content);
        Assert.Equal("the full answer", deltas[2].Content);

        // Sequences stay monotonic so the client cannot mistake the retry for a
        // duplicate frame and silently drop it.
        var sequences = deltas.Select(delta => delta.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(value => value).ToArray(), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
    }

    [Fact]
    public async Task Retries_rejected_tool_call_with_feedback_note()
    {
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? Deltas(new ModelDelta(
                null,
                [new ModelToolFragment("call-8", "read", "{\"path\":")],
                completed: true,
                finishReason: "tool_calls"))
            : Deltas(new ModelDelta("recovered", [], completed: true, finishReason: "stop")));

        var result = await Runner(provider).RunStepAsync(
            Request(ContextWithReadCapability()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal("recovered", result.Step.Text);
        Assert.Equal(2, provider.Calls);
        var secondPrompt = provider.Requests[1].Prompt;
        Assert.Contains("[harness]", secondPrompt, StringComparison.Ordinal);
        Assert.Contains("arguments were incomplete or invalid", secondPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auth_failed", "provider_auth")]
    [InlineData("quota_exhausted", "provider_quota_exhausted")]
    public async Task Does_not_retry_non_retryable_provider_error(string providerCode, string expectedCode)
    {
        var provider = new ScriptedProvider((_, _) =>
            Throws(new ModelProviderException(providerCode, retryable: false)));

        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            Runner(provider).RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None));

        // A non-retryable provider failure keeps its stable code for the run.
        Assert.Equal(expectedCode, error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Gives_up_after_bounded_retryable_failures_with_the_provider_code()
    {
        // The base socket runner surfaced an exhausted transient through the
        // stable carrier, so the run kept provider_rate_limited instead of
        // collapsing to model_failed (WorkerV2ClientTests@71266c9a:1345-1349).
        var provider = new ScriptedProvider((_, _) =>
            Throws(new ModelProviderException("rate_limited", retryable: true)));

        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            Runner(provider).RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("provider_rate_limited", error.Code);
        Assert.Equal("provider_rate_limited", error.StepErrorCode);
        Assert.True(error.Retryable);
        Assert.Equal(10, provider.Calls);
    }

    [Fact]
    public void Default_retry_backoffs_escalate_to_the_ten_attempt_budget()
    {
        // About twelve minutes of waiting before the step parks for Continue, so an unattended
        // long run rides out a short provider outage instead of stopping on it.
        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
             TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180),
             TimeSpan.FromSeconds(300)],
            InProcessModelStepRunner.DefaultStepRetryBackoffs);
        Assert.Equal(10, InProcessModelStepRunner.DefaultStepRetryBackoffs.Count + 1);
    }

    [Fact]
    public void Retry_delay_never_shortens_server_minimum_and_bounds_local_backoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), InProcessModelStepRunner.ComputeRetryDelay(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), jitterSample: 0.5));
        Assert.Equal(TimeSpan.FromHours(1), InProcessModelStepRunner.ComputeRetryDelay(
            TimeSpan.FromSeconds(2), TimeSpan.FromHours(1), jitterSample: 1));
        Assert.Equal(TimeSpan.FromSeconds(1.6), InProcessModelStepRunner.ComputeRetryDelay(
            TimeSpan.FromSeconds(2), null, jitterSample: 0));
    }

    [Fact]
    public async Task Retry_after_is_carried_into_the_bounded_retry_and_attempt_count()
    {
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? Throws(new ModelProviderException("rate_limited", retryable: true, retryAfter: TimeSpan.FromMilliseconds(1)))
            : Deltas(new ModelDelta("ok", [], completed: true, finishReason: "stop")));
        var result = await Runner(provider).RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(2, result.ProviderAttempts);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Long_server_delay_parks_without_an_early_automatic_second_attempt()
    {
        // Beyond the five-minute automatic wait budget the hint is kept and the step parks.
        var provider = new ScriptedProvider((_, _) => Throws(new ModelProviderException(
            "rate_limited", true, retryAfter: TimeSpan.FromMinutes(10))));
        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            Runner(provider).RunStepAsync(Request(), _ => ValueTask.CompletedTask, default));
        Assert.Equal("provider_rate_limited", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.IsType<ModelStepException>(error.InnerException).RetryAfter);
    }

    [Fact]
    public async Task Cancellation_interrupts_retry_after_wait_before_the_next_provider_attempt()
    {
        var provider = new ScriptedProvider((_, _) =>
            Throws(new ModelProviderException("rate_limited", retryable: true, retryAfter: TimeSpan.FromSeconds(30))));
        using var cancellation = new CancellationTokenSource();
        var retryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Runner(provider);
        runner.RetryJitterSample = () => { retryReady.TrySetResult(); return 0.5; };
        var operation = runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, cancellation.Token);
        await retryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Cancellation_before_a_provider_error_is_observed_keeps_cancellation_semantics()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider((_, _) =>
        {
            cancellation.Cancel();
            return Throws(new ModelProviderException("rate_limited", retryable: true));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(provider).RunStepAsync(
            Request(), _ => ValueTask.CompletedTask, cancellation.Token));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Provider_bootstrap_is_resolved_per_attempt_and_reaches_the_provider()
    {
        var bootstraps = 0;
        var provider = new ScriptedProvider((_, _) =>
            Deltas(new ModelDelta("ok", [], completed: true, finishReason: "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) =>
            {
                bootstraps++;
                return Task.FromResult(Bootstrap());
            },
        };

        var result = await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("ok", result.Step.Text);
        Assert.Equal(1, bootstraps);
        Assert.Equal("model-b", provider.Requests[0].Model);
        Assert.Equal("step-secret", provider.Requests[0].Credential);
        Assert.Equal(new Uri("https://step-provider.invalid/v1"), provider.Requests[0].Endpoint);
    }

    [Fact]
    public async Task Provider_selection_failure_keeps_its_stable_code()
    {
        var provider = new ScriptedProvider((_, _) =>
            Deltas(new ModelDelta("never", [], completed: true, finishReason: "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) =>
                Task.FromException<ProviderBootstrapData>(new ProviderCatalogException("catalog_unauthorized")),
        };

        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("catalog_unauthorized", error.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Run_images_are_decoded_from_data_urls_for_the_provider()
    {
        var provider = new ScriptedProvider((_, _) =>
            Deltas(new ModelDelta("mo ta", [], completed: true, finishReason: "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap(acceptsImageInput: true)),
            LoadRunImagesAsync = (_, _) =>
                Task.FromResult(new RunImageSet(["data:image/png;base64,aGVsbG8="], [])),
        };

        var result = await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("mo ta", result.Step.Text);
        var image = Assert.Single(provider.Requests[0].Images);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("aGVsbG8=", image.Base64Data);
    }

    [Fact]
    public async Task Earlier_conversation_images_are_attached_with_a_reference_notice()
    {
        var provider = new ScriptedProvider((_, _) =>
            Deltas(new ModelDelta("mo ta", [], completed: true, finishReason: "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap(acceptsImageInput: true)),
            LoadRunImagesAsync = (_, _) =>
                Task.FromResult(new RunImageSet(Array.Empty<string>(),
                    ["data:image/png;base64,aGVsbG8="])),
        };

        await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);

        var request = provider.Requests[0];
        Assert.Contains("Earlier conversation images re-attached for reference: 1 image",
            request.Prompt, StringComparison.Ordinal);
        Assert.Contains("not attachments of the current message", request.Prompt, StringComparison.Ordinal);
        Assert.Single(request.Images);
    }

    [Fact]
    public async Task Unreadable_image_payloads_fail_the_step_before_the_provider_call()
    {
        var provider = new ScriptedProvider((_, _) =>
            Deltas(new ModelDelta("never", [], completed: true, finishReason: "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
            LoadRunImagesAsync = (_, _) => throw new InvalidOperationException("payload store unavailable"),
        };

        var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("image_payload_unavailable", error.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Prepared_step_retries_a_retryable_provider_error_with_a_fresh_prepare()
    {
        // The production coordinator hands the runner a prepared step; a retryable
        // provider failure must reprepare on the next attempt instead of failing
        // closed as invalid_request.
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? Throws(new ModelProviderException("rate_limited", retryable: true))
            : Deltas(new ModelDelta("recovered after prepare", [], completed: true, finishReason: "stop")));
        var runner = Runner(provider);
        var request = Request();
        var prepared = await runner.PrepareAsync(request, CancellationToken.None);

        var result = await runner.RunStepAsync(
            request.WithPreparation(prepared),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal("recovered after prepare", result.Step.Text);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task An_anthropic_gateway_step_posts_messages_with_its_key_header()
    {
        // ADR 0052: a Models.dev provider published with @ai-sdk/anthropic is called the way
        // Anthropic is, at {api}/messages with x-api-key, never at /chat/completions.
        var gateway = new RecordingGateway();
        var bootstrap = new ProviderBootstrapData("models.dev:minimax", ProviderProtocols.AnthropicMessages,
            new Uri("https://api.minimax.io/anthropic/v1/messages"), "MiniMax-M2", "synthetic-gateway-key",
            maximumOutputTokens: 8_192, contextWindowTokens: 204_800, outputTokenReserve: 8_192);
        var runner = new InProcessModelStepRunner(new ModelStepService(new AnthropicMessagesModelProvider(new HttpClient(gateway))))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(bootstrap),
        };

        var failure = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
            runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("provider_auth", failure.Code);
        var sent = Assert.Single(gateway.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(new Uri("https://api.minimax.io/anthropic/v1/messages"), sent.Uri);
        Assert.Equal("synthetic-gateway-key", sent.ApiKey);
        Assert.False(sent.HasAuthorization);
        Assert.Contains("\"model\":\"MiniMax-M2\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\"", sent.Body, StringComparison.Ordinal);
    }

    private sealed class RecordingGateway : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? ApiKey, bool HasAuthorization, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!,
                request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null,
                request.Headers.Authorization is not null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid key\"}}"),
            };
        }
    }

    [Fact]
    public async Task Unpaired_stored_history_is_repaired_before_dispatch_instead_of_failing()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta("ok", [], completed: true, finishReason: "stop")));
        var repairs = new List<string>();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
            ObserveHistoryRepair = repairs.Add,
        };
        using var arguments = System.Text.Json.JsonDocument.Parse("{}");
        var context = new ModelContextSnapshot(
            [
                ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "Read a file"),
                ModelContextEntry.Assistant("src-2", 2, new AssistantStep(
                    null,
                    [new ModelToolCall("call_a", "read", "1.0.0", arguments.RootElement.Clone())],
                    AssistantFinishReason.ToolCalls,
                    16)),
                ModelContextEntry.Text("src-3", 3, ModelContextRole.User, "prompt"),
            ],
            Array.Empty<byte>());
        var request = new ModelStepRequest(
            new RunId("run_" + new string('a', 32)),
            new ModelStepId("step_" + new string('b', 32)),
            context,
            SystemPromptContext.Empty,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('c', 64));

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("ok", result.Step.Text);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(["model_history_invalid"], repairs);
    }

    [Fact]
    public async Task Missing_prompt_in_stored_context_is_recovered_with_a_continuation_prompt()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta("ok", [], completed: true, finishReason: "stop")));
        var repairs = new List<string>();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
            ObserveHistoryRepair = repairs.Add,
        };
        var context = new ModelContextSnapshot(
            [ModelContextEntry.Text("src-1", 1, ModelContextRole.System, "Working state")],
            Array.Empty<byte>());
        var request = new ModelStepRequest(
            new RunId("run_" + new string('a', 32)),
            new ModelStepId("step_" + new string('b', 32)),
            context,
            SystemPromptContext.Empty,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('c', 64));

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("ok", result.Step.Text);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(["missing_user_prompt"], repairs);
    }

    [Fact]
    public async Task Unknown_finish_reason_is_retried_as_a_malformed_response()
    {
        var provider = new ScriptedProvider((_, attempt) => Deltas(new ModelDelta(
            "answer", [], completed: true, finishReason: attempt == 1 ? "mystery" : "stop")));
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
        };

        var result = await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("answer", result.Step.Text);
        Assert.Equal(AssistantFinishReason.Stop, result.Step.FinishReason);
        Assert.Equal(2, provider.Calls);
    }

    private static InProcessModelStepRunner Runner(
        ScriptedProvider provider, ProviderBootstrapData? bootstrap = null) =>
        new(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(bootstrap ?? Bootstrap()),
        };

    private static ProviderBootstrapData Bootstrap(
        bool acceptsImageInput = false,
        int? contextWindowTokens = 32_768,
        int? outputTokenReserve = null) => new(
        "Kilo",
        "chat_completions",
        new Uri("https://step-provider.invalid/v1"),
        "model-b",
        "step-secret",
        contextWindowTokens: contextWindowTokens,
        outputTokenReserve: outputTokenReserve,
        acceptsImageInput: acceptsImageInput);

    private static ModelStepRequest Request(SystemPromptContext? promptContext = null) => new(
        new RunId("run_" + new string('a', 32)),
        new ModelStepId("step_" + new string('b', 32)),
        new ModelContextSnapshot(
            [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
            Array.Empty<byte>()),
        promptContext ?? SystemPromptContext.Empty,
        AgentPermissionPreset.Standard,
        policySnapshotVersion: 1,
        policySnapshotSha256: new string('c', 64));

    private static SystemPromptContext ContextWithReadCapability()
    {
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
        var capability = new PromptCapability(
            definition.ToolId,
            definition.ToolVersion,
            definition.Description,
            definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
            definition.ExampleArguments?.ToJsonString(),
            "enabled");
        return new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            "ru-agent",
            "standard",
            new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-09-30", "LLM"),
            Array.Empty<PromptInstructionBlock>(),
            [capability],
            new PromptRuntimeContext(Array.Empty<PromptReference>()));
    }

    private static async IAsyncEnumerable<ModelDelta> Deltas(params ModelDelta[] deltas)
    {
        await Task.Yield();
        foreach (var delta in deltas)
        {
            yield return delta;
        }
    }

    private static async IAsyncEnumerable<ModelDelta> Throws(ModelProviderException error)
    {
        await Task.Yield();
        if (error is null)
        {
            yield break;
        }

        throw error;
    }

    private static async IAsyncEnumerable<ModelDelta> DeltasThenThrows(
        ModelDelta delta,
        ModelProviderException error)
    {
        await Task.Yield();
        yield return delta;
        if (error is null)
        {
            yield break;
        }

        throw error;
    }

    private sealed class ScriptedProvider(Func<ModelRequest, int, IAsyncEnumerable<ModelDelta>> script)
        : IModelProvider, IModelRequestBodyProjector
    {
        private int _calls;

        public List<ModelRequest> Requests { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public string ProjectRequestBody(ModelRequest request) =>
            new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = request.Model,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(),
            }.ToJsonString();

        public IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return script(request, Interlocked.Increment(ref _calls));
        }
    }
}
