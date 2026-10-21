using System.Net;
using System.Text;
using System.Text.Json;
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

        Assert.Empty(settings.OpenAiModel);
        Assert.Empty(settings.AnthropicModel);
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
    public async Task Reasoning_save_is_partial_without_invalidating_provider_catalog()
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
        Assert.Equal(0, notifications);
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
    public async Task Failed_provider_model_selection_does_not_raise_settings_saved()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"error\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.False(await api.SaveProviderModelSelectionAsync("openai", "openai-model"));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Provider_model_selection_serializes_only_the_two_field_body()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"success\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.True(await api.SaveProviderModelSelectionAsync("openai", "openai-model"));
        using (var document = JsonDocument.Parse(handler.LastBody))
        {
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        }

        Assert.Contains("\"backend_choice\":\"openai\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"selected_model\":\"openai-model\"", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("custom_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("openai_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic_model", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Provider_removal_posts_only_the_backend_choice_and_notifies_on_success()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"success\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.True(await api.RemoveProviderAsync("openai"));
        using (var document = JsonDocument.Parse(handler.LastBody))
        {
            var property = Assert.Single(document.RootElement.EnumerateObject());
            Assert.Equal("backend_choice", property.Name);
            Assert.Equal("openai", property.Value.GetString());
        }

        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Failed_provider_removal_does_not_raise_settings_saved()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"error\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);
        var notifications = 0;
        api.SettingsSaved += (_, _) => notifications++;

        Assert.False(await api.RemoveProviderAsync("openai"));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void SettingsView_exposes_a_sign_out_button_for_every_picker_row_it_renders()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var named = GuiSource.NamedElements(settings);

        // Only the custom endpoint is a fixed row now, so it carries the only fixed
        // sign-out control. Catalog providers sign out through the active key row.
        Assert.True(named.ContainsKey("CustomSignOutButton"));
        foreach (string retired in new[] { "OpenAiSignOutButton", "AnthropicSignOutButton" })
        {
            Assert.False(named.ContainsKey(retired));
        }

        var button = named["CustomSignOutButton"];
        Assert.Equal("{x:Bind ViewModel.RemoveProviderCommand}", GuiSource.AttributeValue(button, "Command"));
        Assert.Equal("custom", GuiSource.AttributeValue(button, "CommandParameter"));
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

    [Fact]
    public void ProviderReasoningOptions_returns_default_label_and_no_fabricated_options()
    {
        // When model has no reasoning capability or no info
        var unsupportedState = new ProviderCatalogState
        {
            Status = "ok",
            SelectedModel = "model-no-reasoning",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "model-no-reasoning",
                    Supported = true,
                    Reasoning = new ProviderReasoningCapabilities { State = "unsupported" }
                }
            }
        };

        var noReasoningOptions = ProviderReasoningOptions.For(unsupportedState);
        Assert.Single(noReasoningOptions);
        Assert.Equal("default", noReasoningOptions[0].Value);
        Assert.Equal("default", noReasoningOptions[0].Label);

        // When provider explicitly returns levels
        var supportedState = new ProviderCatalogState
        {
            Status = "ok",
            SelectedModel = "model-with-reasoning",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "model-with-reasoning",
                    Supported = true,
                    Reasoning = new ProviderReasoningCapabilities
                    {
                        State = ProviderReasoningCapabilities.Levels,
                        Options = new[] { "low", "medium", "high" }
                    }
                }
            }
        };

        var supportedOptions = ProviderReasoningOptions.For(supportedState);
        Assert.Equal(4, supportedOptions.Count);
        Assert.Equal(new[] { "default", "low", "medium", "high" }, supportedOptions.Select(o => o.Value));
        Assert.Equal(new[] { "default", "low", "medium", "high" }, supportedOptions.Select(o => o.Label));
    }

    [Fact]
    public void Clamp_keeps_a_stored_effort_until_the_catalog_can_judge_it()
    {
        // What the view sees before the catalog lands: one "default" option and
        // nothing to validate against. Clamping here erased a choice the backend
        // had stored, so a selection that saved correctly still snapped the button
        // back to "default" on the next UI update.
        var notLoaded = new List<ProviderReasoningOption> { new("default", "default") };

        Assert.Equal("high", ProviderReasoningOptions.Clamp("high", notLoaded, isCatalogReady: false));
        Assert.Equal("high", ProviderReasoningOptions.Clamp("high", notLoaded, isCatalogReady: false));
    }

    [Fact]
    public void Clamp_drops_an_effort_the_loaded_catalog_does_not_offer()
    {
        // Once the catalog is there the original behaviour stands: a model with
        // no such level genuinely falls back to "default".
        var loaded = new List<ProviderReasoningOption>
        {
            new("default", "default"), new("low", "low"), new("high", "high"),
        };

        Assert.Equal("high", ProviderReasoningOptions.Clamp("high", loaded, isCatalogReady: true));
        Assert.Equal("default", ProviderReasoningOptions.Clamp("xhigh", loaded, isCatalogReady: true));
    }

    private static ProviderCatalogState CatalogState(
        string backend,
        string status = "ok",
        bool stale = false,
        string effort = "high") => new()
        {
            Status = status,
            Backend = backend,
            Identity = "identity",
            SelectedEffort = effort,
            Stale = stale,
        };

    [Fact]
    public void SelectedCatalog_prefers_the_single_controller_for_the_selected_backend()
    {
        // The single controller is pointed at the selected backend and refreshed
        // on load; the grouped entry for the same backend may be older.
        var single = CatalogState("models.dev:openrouter", effort: "high");
        var grouped = new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal)
        {
            ["models.dev:openrouter"] = CatalogState("models.dev:openrouter", effort: "low"),
        };

        Assert.True(ProviderSelectedCatalog.TryResolve(
            single, grouped, "models.dev:openrouter", out var resolved));
        Assert.Equal("high", resolved!.SelectedEffort);
    }

    [Fact]
    public void SelectedCatalog_falls_back_to_the_grouped_entry()
    {
        // Right after a model switch the single refresh has not landed yet, or
        // Settings repointed the shared controller elsewhere: the grouped child
        // still describes the selected backend.
        var single = CatalogState("openai");
        var grouped = new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal)
        {
            ["models.dev:openrouter"] = CatalogState("models.dev:openrouter", effort: "medium"),
        };

        Assert.True(ProviderSelectedCatalog.TryResolve(
            single, grouped, "models.dev:openrouter", out var resolved));
        Assert.Equal("medium", resolved!.SelectedEffort);
    }

    [Fact]
    public void SelectedCatalog_says_nothing_without_a_usable_catalog()
    {
        // No catalog, a failed one, or a stale one must not validate anything:
        // the caller keeps its effort instead of concluding "default".
        var grouped = new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal)
        {
            ["models.dev:openrouter"] = CatalogState("models.dev:openrouter", status: "error"),
            ["openai"] = CatalogState("openai", stale: true),
        };

        Assert.False(ProviderSelectedCatalog.TryResolve(null, null, "models.dev:openrouter", out _));
        Assert.False(ProviderSelectedCatalog.TryResolve(null, grouped, " ", out _));
        Assert.False(ProviderSelectedCatalog.TryResolve(
            CatalogState("models.dev:openrouter", status: "error"), grouped, "models.dev:openrouter", out _));
        Assert.False(ProviderSelectedCatalog.TryResolve(
            CatalogState("models.dev:openrouter", stale: true), null, "models.dev:openrouter", out _));
        Assert.False(ProviderSelectedCatalog.TryResolve(
            CatalogState("other"), null, "models.dev:openrouter", out _));
    }

    [Fact]
    public void ProviderReasoningOptions_uses_override_model_and_ignores_case()
    {
        var state = new ProviderCatalogState
        {
            Status = "ok",
            SelectedModel = "old-model",
            Catalog = new[]
            {
                new ProviderCatalogModel
                {
                    Id = "New-Model",
                    Supported = true,
                    Reasoning = new ProviderReasoningCapabilities
                    {
                        State = ProviderReasoningCapabilities.Levels,
                        Options = new[] { "LOW", "high", "High", "default" },
                    },
                },
            },
        };

        // Stale child SelectedModel must not hide fresh grouped selection.
        var withOverride = ProviderReasoningOptions.For(state, "new-model");
        Assert.Equal(new[] { "default", "LOW", "high" }, withOverride.Select(o => o.Value));

        // Without override the old model has no entry -> default only.
        Assert.Equal(new[] { "default" }, ProviderReasoningOptions.For(state).Select(o => o.Value));
    }

    [Fact]
    public void SettingsView_does_not_contain_reasoning_and_ChatView_contains_chat_reasoning()
    {
        var settingsXamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "SettingsView.xaml");
        if (File.Exists(settingsXamlPath))
        {
            var settingsXaml = File.ReadAllText(settingsXamlPath);
            Assert.DoesNotContain("ReasoningEffortComboBox", settingsXaml);
            Assert.DoesNotContain("x:Name=\"ReasoningRow\"", settingsXaml);
        }

        var chatXamlPath = Path.Combine("..", "..", "..", "..", "gui-winui3", "Views", "ChatView.xaml");
        if (File.Exists(chatXamlPath))
        {
            var chatXaml = File.ReadAllText(chatXamlPath);
            Assert.Contains("ChatReasoningButton", chatXaml);
            Assert.Contains("ChatReasoningLabel", chatXaml);
            Assert.Contains("ChatReasoningFlyout", chatXaml);
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
