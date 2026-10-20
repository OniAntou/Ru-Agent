using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatModelSelectionPresenterTests
{
    [Fact]
    public async Task Invalid_model_choices_never_reach_persistence()
    {
        var presenter = new ChatModelSelectionPresenter();
        var saves = 0;

        var result = await presenter.SelectAsync(" ", "openai", (_, _) =>
        {
            saves++;
            return Task.FromResult(true);
        });

        Assert.Equal(ModelSelectionSaveResult.Failed, result);
        Assert.Equal(0, saves);
        Assert.Equal(string.Empty, presenter.Backend);
    }

    [Fact]
    public async Task Failed_persistence_keeps_the_current_choice()
    {
        var presenter = new ChatModelSelectionPresenter();
        presenter.Adopt("openai", "gpt-4o");

        var result = await presenter.SelectAsync("claude-3-7", "models.dev:anthropic", (_, _) => Task.FromResult(false));

        Assert.Equal(ModelSelectionSaveResult.Failed, result);
        Assert.Equal("openai", presenter.Backend);
        Assert.Equal("gpt-4o", presenter.Model);
    }

    [Fact]
    public async Task Saved_choice_becomes_current_only_after_the_write_succeeds()
    {
        var presenter = new ChatModelSelectionPresenter();

        var result = await presenter.SelectAsync("gpt-4.1", "openai", (backend, model) =>
            Task.FromResult(backend == "openai" && model == "gpt-4.1"));

        Assert.Equal(ModelSelectionSaveResult.Saved, result);
        Assert.Equal("openai", presenter.Backend);
        Assert.Equal("gpt-4.1", presenter.Model);
    }
}
