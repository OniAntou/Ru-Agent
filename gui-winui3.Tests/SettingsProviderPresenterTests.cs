using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SettingsProviderPresenterTests
{
    [Fact]
    public void Signed_in_rows_are_sorted_first_and_each_group_is_alphabetical()
    {
        var rows = new[]
        {
            new ProviderRow("z", "Zulu") { HasKey = true },
            new ProviderRow("a", "Alpha"),
            new ProviderRow("b", "Beta") { HasKey = true },
        };

        Assert.Equal(
            new[] { "b", "z", "a" },
            SettingsProviderPresenter.OrderSignedInFirst(rows).Select(row => row.Value));
    }

    [Fact]
    public void Filtering_keeps_provider_count_and_no_match_guidance_visible_for_an_empty_result()
    {
        var rows = new[]
        {
            new ProviderRow("custom", "Custom OpenAI Server"),
            new ProviderRow("models.dev:openrouter", "OpenRouter"),
        };

        var result = SettingsProviderPresenter.Filter(rows, "nothing", modelsDevLoadFailed: false);

        Assert.Empty(result.VisibleRows);
        Assert.Equal("PROVIDERS (0 of 2)", result.HeaderText);
        Assert.True(result.HasNoMatches);
    }

    [Fact]
    public void Picker_layout_uses_available_window_space_with_floor_cap_and_unbounded_fallback()
    {
        Assert.Equal(200, SettingsProviderPresenter.LayoutFor(double.PositiveInfinity).Height);
        Assert.Equal(96, SettingsProviderPresenter.LayoutFor(80).Height);
        Assert.Equal(176, SettingsProviderPresenter.LayoutFor(200).Height);
        Assert.Equal(200, SettingsProviderPresenter.LayoutFor(600).Height);
    }

    [Theory]
    [InlineData("custom", true, "", true)]
    [InlineData("custom", false, "", false)]
    [InlineData("models.dev:OpenRouter", false, "openrouter", true)]
    [InlineData("models.dev:OpenRouter", false, "other", false)]
    [InlineData("openai", false, "openai", false)]
    public void Signed_in_state_uses_the_matching_credential_source(
        string choice,
        bool customHasKey,
        string keyId,
        bool expected)
    {
        Assert.Equal(
            expected,
            SettingsProviderPresenter.IsSignedIn(choice, customHasKey, new[] { keyId }));
    }

    [Fact]
    public async Task Connection_test_saves_pending_values_before_refreshing_and_reads_the_result()
    {
        var order = new List<string>();
        var state = new ProviderCatalogState { Status = "ok", Backend = "kilo" };

        var result = await SettingsProviderPresenter.TestConnectionAsync(
            () => { order.Add("save"); return Task.FromResult(true); },
            () => { order.Add("refresh"); return Task.CompletedTask; },
            () => { order.Add("read"); return state; });

        Assert.Equal(new[] { "save", "refresh", "read" }, order);
        Assert.Same(state, result);
    }

    [Fact]
    public async Task Connection_test_does_not_refresh_when_saving_pending_values_fails()
    {
        bool refreshed = false;
        var result = await SettingsProviderPresenter.TestConnectionAsync(
            () => Task.FromResult(false),
            () => { refreshed = true; return Task.CompletedTask; },
            () => new ProviderCatalogState { Status = "ok" });

        Assert.Null(result);
        Assert.False(refreshed);
    }

    [Fact]
    public async Task Sign_out_clears_and_reloads_only_after_successful_removal()
    {
        var order = new List<string>();
        bool removed = await SettingsProviderPresenter.SignOutAsync(
            "models.dev:openrouter",
            choice => { order.Add("remove:" + choice); return Task.FromResult(true); },
            choice => order.Add("clear:" + choice),
            () => { order.Add("reload"); return Task.CompletedTask; });

        Assert.True(removed);
        Assert.Equal(
            new[] { "remove:models.dev:openrouter", "clear:models.dev:openrouter", "reload" },
            order);
    }

    [Fact]
    public async Task Sign_out_failure_preserves_the_local_key_and_settings()
    {
        var cleared = false;
        var reloaded = false;
        bool removed = await SettingsProviderPresenter.SignOutAsync(
            "models.dev:openrouter",
            _ => Task.FromResult(false),
            _ => cleared = true,
            () => { reloaded = true; return Task.CompletedTask; });

        Assert.False(removed);
        Assert.False(cleared);
        Assert.False(reloaded);
    }
}
