using System.Net;
using System.Text;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Missing_settings_model_fields_do_not_fabricate_gui_fallbacks()
    {
        var settings = new AppSettingsDto();

        Assert.Empty(settings.OpenCodeModel);
        Assert.Empty(settings.KiloModel);
        Assert.Empty(settings.OllamaModel);
        Assert.Empty(settings.LmStudioModel);
        Assert.Empty(settings.CustomModel);
    }

    [Fact]
    public void Contract_preserves_valid_empty_and_reasoning_metadata()
    {
        var snapshot = ProviderCatalogSnapshot.Parse("""
            {
              "status":"ok",
              "backend":"kilo",
              "identity":"acct-1",
              "selected_model":"model-a",
              "selected_effort":"default",
              "models":[],
              "catalog":[{"id":"model-a","supported":true,"protocol":"chat_completions","reasoning":{"state":"levels","options":["low","high"]}}],
              "stale":false,
              "error_code":null,
              "last_success_utc":"2026-09-01T00:00:00Z"
            }
            """);

        Assert.True(snapshot.IsSuccess);
        Assert.True(snapshot.IsValidEmpty);
        Assert.Equal("acct-1", snapshot.Identity);
        Assert.Equal("levels", snapshot.Catalog[0].Reasoning.State);
        Assert.Equal(new[] { "low", "high" }, snapshot.Catalog[0].Reasoning.Options);
    }

    [Fact]
    public void Error_contract_is_not_treated_as_empty_success()
    {
        var snapshot = ProviderCatalogSnapshot.Parse("""
            {"status":"error","backend":"zen","identity":"acct-1","models":[],"error_code":"provider_unavailable","stale":true}
            """);

        Assert.False(snapshot.IsSuccess);
        Assert.False(snapshot.IsValidEmpty);
        Assert.Equal("provider_unavailable", snapshot.ErrorCode);
    }

    [Fact]
    public void Unknown_reasoning_capability_exposes_provider_default_only()
    {
        var state = new ProviderCatalogState
        {
            SelectedModel = "zen-model",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "zen-model",
                    Supported = true,
                    Protocol = "chat_completions",
                    Reasoning = new ProviderReasoningCapabilities { State = "unknown" }
                }
            }
        };

        var options = ProviderReasoningOptions.For(state);

        Assert.Equal(new[] { "default" }, options.Select(option => option.Value));
    }

    [Fact]
    public void Stale_catalog_does_not_expose_explicit_reasoning_as_selectable()
    {
        var state = new ProviderCatalogState
        {
            Status = "error",
            Stale = true,
            SelectedModel = "model-a",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "model-a",
                    Supported = true,
                    Protocol = "chat_completions",
                    Reasoning = new ProviderReasoningCapabilities
                    {
                        State = "levels",
                        Options = new[] { "high" },
                    },
                },
            },
        };

        Assert.Equal(new[] { "default" }, ProviderReasoningOptions.For(state).Select(option => option.Value));
    }

    [Fact]
    public void Unsupported_model_does_not_expose_reasoning_levels()
    {
        var state = new ProviderCatalogState
        {
            Status = "ok",
            SelectedModel = "model-a",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "model-a",
                    Supported = false,
                    Reasoning = new ProviderReasoningCapabilities
                    {
                        State = ProviderReasoningCapabilities.Levels,
                        Options = new[] { "low", "high" }
                    }
                }
            }
        };

        Assert.Equal(new[] { "default" }, ProviderReasoningOptions.For(state).Select(option => option.Value));
    }

    [Fact]
    public async Task Controller_preserves_selection_search_and_rejects_old_generation()
    {
        var firstFetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<ProviderCatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<ProviderCatalogState>();
        var calls = 0;
        await using var controller = new ProviderCatalogController(
            new ProviderCatalogService((backend, refresh, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstFetchStarted.TrySetResult(true);
                    return releaseFirst.Task.ContinueWith(task => (ProviderCatalogSnapshot?)task.Result);
                }

                return Task.FromResult<ProviderCatalogSnapshot?>(ProviderCatalogSnapshot.Success(
                    backend,
                    "new-identity",
                    "model-a",
                    "default",
                    new[] { "model-a" }));
            }),
            state => applied.Add(state));

        controller.SetBackend("kilo");
        controller.SetSearchText("keep-me");
        var staleRefresh = controller.RefreshAsync();
        await firstFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Invalidate();
        releaseFirst.TrySetResult(ProviderCatalogSnapshot.Success(
            "kilo", "old-identity", "old-provider-model", "default", new[] { "old-provider-model" }));
        Assert.False(await staleRefresh);

        Assert.True(await controller.RefreshAsync(force: true));
        Assert.Equal("keep-me", controller.State.SearchText);
        Assert.DoesNotContain("old-provider-model", controller.State.Models);
        Assert.Equal("model-a", controller.State.SelectedModel);
        Assert.Equal("model-a", applied[^1].SelectedModel);
    }

    [Fact]
    public async Task Missing_backend_error_becomes_stale_error_for_requested_backend()
    {
        var calls = 0;
        await using var controller = new ProviderCatalogController(
            new ProviderCatalogService((backend, refresh, cancellationToken) =>
            {
                calls++;
                return Task.FromResult<ProviderCatalogSnapshot?>(calls == 1
                    ? ProviderCatalogSnapshot.Success(backend, "identity", "model-a", "default", new[] { "model-a" })
                    : new ProviderCatalogSnapshot
                    {
                        Status = "error",
                        ErrorCode = "provider_not_active",
                        Models = Array.Empty<string>()
                    });
            }));

        controller.SetBackend("kilo");
        Assert.True(await controller.RefreshAsync());
        Assert.True(await controller.RefreshAsync());

        Assert.Equal("kilo", controller.State.Backend);
        Assert.False(controller.State.IsSuccess);
        Assert.True(controller.State.Stale);
        Assert.Equal("provider_not_active", controller.State.ErrorCode);
        Assert.Equal(new[] { "model-a" }, controller.State.Models);
    }

    [Fact]
    public async Task Controller_coalesces_single_flight_and_throttles_forced_refresh()
    {
        var now = new TestClock(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var release = new TaskCompletionSource<ProviderCatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var controller = new ProviderCatalogController(
            new ProviderCatalogService((backend, refresh, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                return release.Task.ContinueWith(task => (ProviderCatalogSnapshot?)task.Result);
            }),
            _ => { },
            now);

        controller.SetBackend("zen");
        var first = controller.RefreshAsync(force: true);
        var second = controller.RefreshAsync(force: true);
        Assert.Same(first, second);
        Assert.Equal(1, calls);

        release.TrySetResult(ProviderCatalogSnapshot.Success(
            "zen", "identity", "model-a", "default", new[] { "model-a" }));
        Assert.True(await first);
        Assert.False(await controller.RefreshAsync(force: true));

        now.Advance(TimeSpan.FromSeconds(31));
        release = new TaskCompletionSource<ProviderCatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = controller.RefreshAsync(force: true);
        release.TrySetResult(ProviderCatalogSnapshot.Success(
            "zen", "identity", "model-b", "default", new[] { "model-b" }));
        Assert.True(await refreshed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Controller_polls_only_when_active_and_only_after_five_minutes()
    {
        var now = new TestClock(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var calls = 0;
        await using var controller = new ProviderCatalogController(
            new ProviderCatalogService((backend, refresh, cancellationToken) =>
            {
                calls++;
                return Task.FromResult<ProviderCatalogSnapshot?>(new ProviderCatalogSnapshot
                {
                    Status = "ok",
                    Backend = backend,
                    Identity = "identity",
                    SelectedModel = "model-a",
                    Models = new[] { "model-a" },
                    LastSuccessUtc = now.UtcNow
                });
            }),
            clock: now);

        controller.SetBackend("zen");
        Assert.False(await controller.RefreshIfDueAsync());
        controller.SetPollingActive(true);
        Assert.True(await controller.RefreshIfDueAsync());
        Assert.False(await controller.RefreshIfDueAsync());
        now.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await controller.RefreshIfDueAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Controller_dispose_cancels_an_inflight_catalog_fetch()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new ProviderCatalogController(
            new ProviderCatalogService(async (backend, refresh, cancellationToken) =>
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult(true);
                    throw;
                }
                return null;
            }));

        controller.SetBackend("zen");
        var refresh = controller.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.DisposeAsync();

        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await refresh);
    }

    [Fact]
    public async Task ApiService_reads_catalog_and_honors_cancellation()
    {
        using var handler = new CatalogHandler("""
            {"status":"ok","backend":"zen","identity":"id","selected_model":"model-a","selected_effort":"default","models":["model-a"],"catalog":[],"stale":false}
            """);
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);

        var snapshot = await api.GetProviderCatalogAsync("zen", refresh: true, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("model-a", snapshot!.Models.Single());
        Assert.Contains("refresh=true", handler.RequestUri!.Query);
        Assert.Equal("Bearer secret", handler.Authorization);
    }

    [Fact]
    public async Task SaveSettings_requires_ok_envelope_and_notifies_only_on_success()
    {
        using var handler = new SaveHandler(
            new[] { "{\"status\":\"error\",\"error_code\":\"invalid\"}", "{\"status\":\"ok\"}", "{\"status\":\"success\"}", "" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.False(await api.SaveSettingsAsync(new AppSettingsDto()));
        Assert.True(await api.SaveSettingsAsync(new AppSettingsDto()));
        Assert.True(await api.SaveSettingsAsync(new AppSettingsDto()));
        Assert.Equal(2, notifications);
        Assert.False(await api.SaveSettingsAsync(new AppSettingsDto()));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Reasoning_save_is_partial_and_emits_invalidation_event()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"ok\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.True(await api.SaveReasoningSettingsAsync("high", "model-a", "identity"));
        Assert.Contains("reasoning_effort", handler.LastBody);
        Assert.DoesNotContain("backend_choice", handler.LastBody);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Default_reasoning_save_sends_the_reset_effort()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"success\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);

        Assert.True(await api.SaveReasoningSettingsAsync("default", "model-a", "identity"));
        Assert.Contains("\"reasoning_effort\":\"default\"", handler.LastBody);
        Assert.Contains("reasoning_model", handler.LastBody);
        Assert.Contains("reasoning_identity", handler.LastBody);
    }

    [Fact]
    public async Task Model_save_serializes_only_the_selected_provider_model()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"success\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);

        Assert.True(await api.SaveModelSettingsAsync("kilo", "kilo-model"));
        Assert.Contains("\"kilo_model\":\"kilo-model\"", handler.LastBody);
        Assert.DoesNotContain("backend_choice", handler.LastBody);
        Assert.DoesNotContain("opencode_model", handler.LastBody);
        Assert.DoesNotContain("ollama_model", handler.LastBody);
        Assert.DoesNotContain("lmstudio_model", handler.LastBody);
        Assert.DoesNotContain("custom_model", handler.LastBody);
    }

    private sealed class TestClock : IProviderCatalogClock
    {
        public TestClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; private set; }
        public void Advance(TimeSpan amount) => UtcNow += amount;
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        private readonly string _json;
        public CatalogHandler(string json) => _json = json;
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SaveHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public SaveHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
