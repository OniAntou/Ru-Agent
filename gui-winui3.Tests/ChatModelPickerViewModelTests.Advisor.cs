using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>ADR 0047: the composer's /advisor command picks the advisor with the model picker.</summary>
public sealed partial class ChatModelPickerViewModelTests
{
    [Fact]
    public async Task Picking_the_advisor_saves_the_pair_and_leaves_the_main_model_alone()
    {
        var (viewModel, backend, _, grouped) = NewViewModel();
        await viewModel.LoadAsync();
        viewModel.BeginAdvisorPick();

        var status = await viewModel.SelectAdvisorAsync("advisor-model", "models.dev:anthropic");

        Assert.Equal(("models.dev:anthropic", "advisor-model"), Assert.Single(backend.AdvisorSaves));
        Assert.Empty(backend.ModelSaves);
        Assert.Equal(Model, viewModel.ModelLabel);
        Assert.Equal((Backend, Model), grouped.Selection);
        Assert.Contains("advisor-model", status, StringComparison.Ordinal);
        Assert.EndsWith("Applies to new runs.", status, StringComparison.Ordinal);
        Assert.StartsWith("advisor-model on ", viewModel.AdvisorLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_the_advisor_off_clears_the_pair_and_a_failed_save_keeps_the_previous_choice()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        backend.Settings.AdvisorBackend = "models.dev:openai";
        backend.Settings.AdvisorModel = "gpt-advisor";
        await viewModel.LoadAsync();
        backend.AdvisorSaveSucceeds = false;

        var failed = await viewModel.ClearAdvisorAsync();

        Assert.StartsWith("Could not", failed, StringComparison.Ordinal);
        Assert.NotNull(viewModel.AdvisorLabel);
        backend.AdvisorSaveSucceeds = true;

        var cleared = await viewModel.ClearAdvisorAsync();

        Assert.StartsWith("Advisor off.", cleared, StringComparison.Ordinal);
        Assert.Equal((null, null), backend.AdvisorSaves[^1]);
        Assert.Null(viewModel.AdvisorLabel);
    }

    [Fact]
    public async Task The_advisor_pick_marks_the_stored_advisor_and_ends_when_the_picker_closes()
    {
        var (viewModel, backend, _, grouped) = NewViewModel();
        grouped.CurrentDefinitions = [.. Models.ProviderGroupDefinitions.All, new(Backend, Backend, "OpenAI", 99)];
        backend.Settings.AdvisorBackend = Backend;
        backend.Settings.AdvisorModel = "not-listed-advisor";
        await viewModel.LoadAsync();
        grouped.Publish(Model, withCatalog: true);
        Models.ProviderModelPickerProjection? tree = null;
        viewModel.PickerProjectionReady += projection => tree = projection;

        viewModel.BeginAdvisorPick();
        viewModel.OnPickerOpened();

        var rows = tree!.Groups.SelectMany(group => group.Rows).ToArray();
        Assert.False(rows.Single(row => row.ModelId == Model).IsSelected);
        Assert.True(rows.Single(row => row.ModelId == "not-listed-advisor").IsSelected);

        viewModel.OnPickerClosed();
        Assert.False(viewModel.IsPickingAdvisor);
        viewModel.OnPickerOpened();
        rows = tree!.Groups.SelectMany(group => group.Rows).ToArray();
        Assert.True(rows.Single(row => row.ModelId == Model).IsSelected);
    }

    [Theory]
    [InlineData("/advisor", ComposerAdvisorCommand.Pick)]
    [InlineData("  /Advisor  ", ComposerAdvisorCommand.Pick)]
    [InlineData("/advisor off", ComposerAdvisorCommand.Off)]
    [InlineData("/advisor OFF", ComposerAdvisorCommand.Off)]
    [InlineData("/advisor claude-opus-5-5", ComposerAdvisorCommand.Usage)]
    [InlineData("/advisor off now", ComposerAdvisorCommand.Usage)]
    public void The_advisor_command_is_parsed_from_the_sent_line(string text, ComposerAdvisorCommand expected) =>
        Assert.Equal(expected, ComposerSlashCommands.ParseAdvisor(text));

    [Theory]
    [InlineData("/advisors")]
    [InlineData("ask the /advisor")]
    [InlineData("")]
    public void Other_text_is_not_the_advisor_command(string text) =>
        Assert.Null(ComposerSlashCommands.ParseAdvisor(text));

    [Fact]
    public void The_slash_menu_offers_advisor_as_an_app_command()
    {
        var row = Assert.Single(ComposerSlashCommands.Suggest("/adv", workspace: null, userHome: null));

        Assert.Equal("advisor", row.Name);
        Assert.True(row.IsBuiltIn);
    }
}
