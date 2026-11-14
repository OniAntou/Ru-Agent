using RuApp.Models;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

/// <summary>ADR 0047: the advisor card offers connected providers and saves the pair once a model is chosen.</summary>
public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task Advisor_starts_off_and_offers_the_connected_providers()
    {
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true, HasAnthropicKey = true } };
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.True(viewModel.AdvisorProvider!.IsOff);
        Assert.False(viewModel.IsAdvisorModelVisible);
        Assert.Equal("Off", viewModel.AdvisorProviders[0].DisplayName);
        Assert.Contains(viewModel.AdvisorProviders, option => option.Choice == "anthropic");
        Assert.Contains(viewModel.AdvisorProviders, option => option.Choice == "openai");
        Assert.Empty(backend.AdvisorSaves);
    }

    [Fact]
    public async Task A_stored_advisor_is_shown_against_the_provider_choice_it_normalizes_from()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto
            {
                BackendChoice = "openai", HasOpenAiKey = true, HasAnthropicKey = true,
                AdvisorBackend = "Anthropic", AdvisorModel = "claude-opus-5-5",
            },
        };
        backend.Models["anthropic"] = ["claude-opus-5-5", "claude-sonnet-5-5"];
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.Equal("anthropic", viewModel.AdvisorProvider!.Choice);
        Assert.Equal("claude-opus-5-5", viewModel.AdvisorModel);
        Assert.True(viewModel.IsAdvisorModelVisible);
        Assert.Contains("claude-sonnet-5-5", viewModel.AdvisorModels);
        Assert.Contains("new runs", viewModel.AdvisorStatus);
        Assert.Empty(backend.AdvisorSaves);
    }

    [Fact]
    public async Task Choosing_a_provider_saves_nothing_until_a_model_is_committed()
    {
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true, HasAnthropicKey = true } };
        backend.Models["anthropic"] = ["claude-opus-5-5"];
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.AdvisorProvider = viewModel.AdvisorProviders.Single(option => option.Choice == "anthropic");
        Assert.Empty(backend.AdvisorSaves);
        Assert.Equal("Choose a model to finish.", viewModel.AdvisorStatus);
        Assert.Equal(["claude-opus-5-5"], viewModel.AdvisorModels);

        await viewModel.CommitAdvisorModelCommand.ExecuteAsync(" claude-opus-5-5 ");

        Assert.Equal(("anthropic", "claude-opus-5-5"), Assert.Single(backend.AdvisorSaves));
        Assert.Contains("new runs", viewModel.AdvisorStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    public async Task A_blank_or_spaced_model_is_not_saved(string model)
    {
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true } };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());
        viewModel.AdvisorProvider = viewModel.AdvisorProviders.Single(option => option.Choice == "openai");

        await viewModel.CommitAdvisorModelCommand.ExecuteAsync(model);

        Assert.Empty(backend.AdvisorSaves);
    }

    [Fact]
    public async Task Choosing_off_clears_the_advisor_and_a_failed_save_says_so()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto
            {
                BackendChoice = "openai", HasOpenAiKey = true,
                AdvisorBackend = "OpenAI", AdvisorModel = "gpt-5",
            },
            AdvisorSaveResult = false,
        };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.AdvisorProvider = viewModel.AdvisorProviders[0];

        Assert.Equal((null, null), Assert.Single(backend.AdvisorSaves));
        Assert.Contains("Could not save", viewModel.AdvisorStatus);
        // The card shows what the Host still holds, not the choice it refused.
        Assert.Equal("openai", viewModel.AdvisorProvider!.Choice);
        Assert.Equal("gpt-5", viewModel.AdvisorModel);
        Assert.True(viewModel.IsAdvisorModelVisible);
    }

    [Fact]
    public async Task Repeated_provider_refreshes_keep_the_selection_published()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto
            {
                BackendChoice = "openai", HasOpenAiKey = true,
                AdvisorBackend = "OpenAI", AdvisorModel = "gpt-5",
            },
        };
        using var viewModel = NewViewModel();
        var selections = new List<string?>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.AdvisorProvider)) selections.Add(viewModel.AdvisorProvider?.Choice);
        };

        await viewModel.AttachAsync(backend, new FakeCatalog());
        var listBefore = viewModel.AdvisorProviders.ToArray();
        await viewModel.LoadAsync();

        Assert.Equal("openai", viewModel.AdvisorProvider!.Choice);
        Assert.Equal(listBefore, viewModel.AdvisorProviders);
        // The last published value is the stored provider, never a cleared one.
        Assert.Equal("openai", selections[^1]);
    }

    [Fact]
    public async Task A_provider_whose_models_cannot_load_still_accepts_a_typed_model()
    {
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true } };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.AdvisorProvider = viewModel.AdvisorProviders.Single(option => option.Choice == "openai");
        Assert.Contains("Type a model ID", viewModel.AdvisorStatus);

        await viewModel.CommitAdvisorModelCommand.ExecuteAsync("gpt-5");
        Assert.Equal(("openai", "gpt-5"), Assert.Single(backend.AdvisorSaves));
    }
}
