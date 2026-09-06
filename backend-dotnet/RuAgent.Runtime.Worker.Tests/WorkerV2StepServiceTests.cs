using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Core.Lifecycle;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerV2StepServiceTests
{
    [Fact]
    public async Task Step_service_composes_once_and_returns_one_done_with_receipt()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("xin ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("chao anh", Array.Empty<ModelToolFragment>(), true, "stop"));
        var result = await new WorkerV2StepService(provider).RunAsync(StartPayload(), CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Equal("xin chao anh", result.Text);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("1.0.0", result.PromptReceipt.ComposerVersion);
        Assert.Equal("## ru.persona", provider.Requests[0].SystemPrompt[..13]);
    }

    [Fact]
    public async Task Step_service_includes_images_in_model_request()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("mo ta", Array.Empty<ModelToolFragment>(), true, "stop"));
        var payload = StartPayload();
        payload["images"] = new JsonArray { "data:image/png;base64,aGVsbG8=" };
        var result = await new WorkerV2StepService(provider).RunAsync(payload, CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Single(provider.Requests[0].Images);
        Assert.Equal("image/png", provider.Requests[0].Images[0].MimeType);
        Assert.Equal("aGVsbG8=", provider.Requests[0].Images[0].Base64Data);
    }

    [Fact]
    public async Task Step_service_accepts_empty_prompt_when_image_present()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("anh dep", Array.Empty<ModelToolFragment>(), true, "stop"));
        var payload = StartPayload();
        payload["prompt"] = "";
        payload["images"] = new JsonArray { "data:image/jpeg;base64,d29ybGQ=" };
        var result = await new WorkerV2StepService(provider).RunAsync(payload, CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Equal("", provider.Requests[0].Prompt);
        Assert.Single(provider.Requests[0].Images);
        Assert.Equal("image/jpeg", provider.Requests[0].Images[0].MimeType);
        Assert.Equal("d29ybGQ=", provider.Requests[0].Images[0].Base64Data);
    }

    [Fact]
    public async Task Step_service_forwards_visible_deltas_without_waiting_for_done()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("xin ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("chao anh", Array.Empty<ModelToolFragment>(), true, "stop"));
        var deltas = new List<string>();

        var result = await new WorkerV2StepService(provider).RunAsync(
            StartPayload(),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("xin chao anh", result.Text);
        Assert.Equal(new[] { "xin ", "chao anh" }, deltas);
    }

    [Fact]
    public async Task Step_service_accumulates_one_tool_call_without_visible_arguments()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\"}") }, true, "tool_calls"));

        var result = await new WorkerV2StepService(provider).RunAsync(StartPayload(includeTool: true), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal("files.read", result.ToolCalls[0].ToolId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("README.md", result.PromptReceipt.PromptSha256, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_service_converts_prompt_tool_selection_without_leaking_json_as_text()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(
                "{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}",
                Array.Empty<ModelToolFragment>(),
                true,
                "stop"));
        var deltas = new List<string>();

        var result = await new WorkerV2StepService(provider).RunAsync(
            StartPayload(includeTool: true),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Empty(deltas);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Single(result.ToolCalls);
        Assert.Null(result.ToolCalls[0].ProviderCallId);
        Assert.Equal("files.read", result.ToolCalls[0].ToolId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_holds_split_prompt_tool_selection_until_it_is_validated()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("{\"tool_id\":\"files.", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}", Array.Empty<ModelToolFragment>(), true, "stop"));
        var deltas = new List<string>();

        var result = await new WorkerV2StepService(provider).RunAsync(
            StartPayload(includeTool: true),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Empty(deltas);
        Assert.Equal("files.read", result.ToolCalls.Single().ToolId);
    }

    [Fact]
    public async Task Step_service_converts_split_tool_call_wrapper_without_leaking_markup()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("<tool_", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("call> {\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}} </tool_call>", Array.Empty<ModelToolFragment>(), true, "stop"));
        var deltas = new List<string>();

        var result = await new WorkerV2StepService(provider).RunAsync(
            StartPayload(includeTool: true),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Empty(deltas);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("files.read", Assert.Single(result.ToolCalls).ToolId);
    }

    [Fact]
    public async Task Step_service_keeps_ordinary_prose_visible_when_tools_are_advertised()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("Đang kiểm tra cho anh.", Array.Empty<ModelToolFragment>(), true, "stop"));

        var result = await new WorkerV2StepService(provider).RunAsync(StartPayload(includeTool: true), CancellationToken.None);

        Assert.Equal("Đang kiểm tra cho anh.", result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task Step_service_rejects_a_malformed_prompt_tool_selection()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("{\"tool_id\":\"files.read\"}", Array.Empty<ModelToolFragment>(), true, "stop"));

        var error = await Assert.ThrowsAsync<WorkerV2StepException>(() =>
            new WorkerV2StepService(provider).RunAsync(StartPayload(includeTool: true), CancellationToken.None));

        Assert.Equal("invalid_selection", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Step_service_observes_cancellation_and_does_not_release_a_running_provider_early()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new BlockingProvider();
        var task = new WorkerV2StepService(provider).RunAsync(StartPayload(), cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(provider.Cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task Step_service_holds_and_releases_the_shared_generation_lease()
    {
        using var cancellation = new CancellationTokenSource();
        await using var leases = new RuntimeLeaseCoordinator();
        var provider = new BlockingProvider();
        var task = new WorkerV2StepService(provider, leases: leases)
            .RunAsync(StartPayload(), cancellation.Token);

        await provider.Started.Task;
        Assert.False(leases.TryAcquire(ResourceKind.Observation, out var blocked));
        Assert.Null(blocked);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(leases.TryAcquire(ResourceKind.Observation, out var released));
        await released!.DisposeAsync();
    }

    [Fact]
    public async Task Step_service_uses_the_per_step_protected_provider_snapshot()
    {
        var root = Directory.CreateTempSubdirectory("ru-worker-bootstrap-");
        try
        {
            var path = ProtectedProviderBootstrap.Write(
                root.FullName,
                new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://step-provider.invalid/v1"),
                    "model-b",
                    "step-secret",
                    new JsonObject
                    {
                        ["reasoning"] = new JsonObject
                        {
                            ["effort"] = "high",
                            ["enabled"] = true,
                        },
                    }));
            var start = StartPayload(providerBootstrapPath: path);
            start["settings"]!["reasoning"] = new JsonObject { ["effort"] = "low", ["enabled"] = true };
            start["settings"]!["reasoning_model"] = "untrusted-model";
            var provider = new FakeModelProvider(new ModelDelta("ok", [], true, "stop"));

            await new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker",
                    new Uri("https://startup-provider.invalid/v1"),
                    "startup-model",
                    "startup-secret",
                    root.FullName))
                .RunAsync(start, CancellationToken.None);

            var request = Assert.Single(provider.Requests);
            Assert.Equal("model-b", request.Model);
            Assert.Equal(new Uri("https://step-provider.invalid/v1"), request.Endpoint);
            Assert.Equal("step-secret", request.Credential);
            Assert.Equal("high", request.Settings["reasoning"]!["effort"]!.GetValue<string>());
            Assert.True(request.Settings["reasoning"]!["enabled"]!.GetValue<bool>());
            Assert.False(request.Settings.ContainsKey("reasoning_model"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Step_service_requires_a_per_step_handoff_when_a_trusted_root_is_configured()
    {
        var root = Directory.CreateTempSubdirectory("ru-worker-bootstrap-");
        try
        {
            var provider = new FakeModelProvider(new ModelDelta("must not run", [], true, "stop"));
            var error = await Assert.ThrowsAsync<WorkerV2StepException>(() => new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker",
                    new Uri("https://startup-provider.invalid/v1"),
                    "startup-model",
                    "startup-secret",
                    root.FullName))
                .RunAsync(StartPayload(), CancellationToken.None));

            Assert.Equal("provider_bootstrap_required", error.Code);
            Assert.Empty(provider.Requests);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Step_service_preserves_provider_default_omission()
    {
        var root = Directory.CreateTempSubdirectory("ru-worker-bootstrap-");
        try
        {
            var path = ProtectedProviderBootstrap.Write(
                root.FullName,
                new ProviderBootstrapData("Zen", "chat_completions", new Uri("https://step-provider.invalid/v1"),
                    "model-a",
                    string.Empty));
            var start = StartPayload(providerBootstrapPath: path);
            start["settings"]!["reasoning_effort"] = "high";
            start["settings"]!["max_tokens"] = 256;
            var provider = new FakeModelProvider(new ModelDelta("ok", [], true, "stop"));

            await new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker",
                    new Uri("https://startup-provider.invalid/v1"),
                    "startup-model",
                    "startup-secret",
                    root.FullName))
                .RunAsync(start, CancellationToken.None);

            var request = Assert.Single(provider.Requests);
            Assert.False(request.Settings.ContainsKey("reasoning_effort"));
            Assert.False(request.Settings.ContainsKey("max_tokens"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Step_service_deletes_a_malformed_owned_handoff_without_dispatching()
    {
        var root = Directory.CreateTempSubdirectory("ru-worker-bootstrap-");
        var path = Path.Combine(root.FullName, "provider-bootstrap-invalid.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var provider = new FakeModelProvider(new ModelDelta("must not run", [], true, "stop"));
            var error = await Assert.ThrowsAsync<WorkerV2StepException>(() => new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker",
                    new Uri("https://startup-provider.invalid/v1"),
                    "startup-model",
                    "startup-secret",
                    root.FullName))
                .RunAsync(StartPayload(providerBootstrapPath: path), CancellationToken.None));

            Assert.Equal("provider_bootstrap_invalid", error.Code);
            Assert.Empty(provider.Requests);
            Assert.False(File.Exists(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Step_service_cleans_an_owned_handoff_when_cancelled_before_dispatch()
    {
        var root = Directory.CreateTempSubdirectory("ru-worker-bootstrap-");
        try
        {
            var path = ProtectedProviderBootstrap.Write(
                root.FullName,
                new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://step-provider.invalid/v1"),
                    "model-a",
                    string.Empty));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var provider = new FakeModelProvider(new ModelDelta("must not run", [], true, "stop"));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkerV2StepService(
                provider,
                new WorkerV2Options(
                    0,
                    "worker",
                    new Uri("https://startup-provider.invalid/v1"),
                    "startup-model",
                    "startup-secret",
                    root.FullName))
                .RunAsync(StartPayload(providerBootstrapPath: path), cancellation.Token));

            Assert.Empty(provider.Requests);
            Assert.False(File.Exists(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static JsonObject StartPayload(bool includeTool = false, string? providerBootstrapPath = null)
    {
        var context = WorkerV2PromptCodec.WriteContext(SystemPromptContext.Empty);
        var settings = new JsonObject();
        if (includeTool)
        {
            settings["model_tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool_id"] = "files.read",
                    ["tool_version"] = "1.0.0",
                    ["scopes"] = new JsonArray("workspace.read"),
                    ["availability"] = "executable",
                    ["model_enabled"] = true,
                    ["capability_class"] = "tool",
                    ["verification_required"] = false,
                },
            };
        }
        var payload = new JsonObject
        {
            ["prompt"] = "xin chào",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = context,
            ["settings"] = settings,
        };
        if (providerBootstrapPath is not null)
        {
            payload["provider_bootstrap_path"] = providerBootstrapPath;
        }

        return payload;
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelDelta> _deltas;
        public List<ModelRequest> Requests { get; } = new();

        public FakeModelProvider(params ModelDelta[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.SetResult();
                throw;
            }

            yield break;
        }
    }
}
