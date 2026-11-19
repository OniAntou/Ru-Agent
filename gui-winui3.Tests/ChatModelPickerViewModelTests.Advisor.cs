using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// ADR 0047: the composer's /advisor command picks this chat's advisor with the model picker.
/// Each session has its own; a new chat has none until one is chosen.
/// </summary>
public sealed partial class ChatModelPickerViewModelTests
{
    [Fact]
    public async Task Picking_the_advisor_saves_the_pair_and_leaves_the_main_model_alone()
    {
        var (viewModel, backend, _, grouped) = NewViewModel();
        await viewModel.LoadAsync();
        viewModel.BeginAdvisorPick();

        var status = await viewModel.SelectAdvisorAsync("advisor-model", "models.dev:anthropic", "0123456789abcdef0123456789abcdef");

        Assert.Equal(("models.dev:anthropic", "advisor-model"), Assert.Single(backend.AdvisorSaves));
        Assert.Equal("0123456789abcdef0123456789abcdef", Assert.Single(backend.AdvisorSessions));
        Assert.Empty(backend.ModelSaves);
        Assert.Equal(Model, viewModel.ModelLabel);
        Assert.Equal((Backend, Model), grouped.Selection);
        Assert.Contains("advisor-model", status, StringComparison.Ordinal);
        Assert.EndsWith("Applies to this chat's next run.", status, StringComparison.Ordinal);
        Assert.StartsWith("advisor-model on ", viewModel.AdvisorLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_the_advisor_off_clears_the_pair_and_a_failed_save_keeps_the_previous_choice()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        await viewModel.LoadAsync();
        viewModel.ShowSessionAdvisor("models.dev:openai", "gpt-advisor");
        backend.AdvisorSaveSucceeds = false;

        var failed = await viewModel.ClearAdvisorAsync("0123456789abcdef0123456789abcdef");

        Assert.StartsWith("Could not", failed, StringComparison.Ordinal);
        Assert.NotNull(viewModel.AdvisorLabel);
        backend.AdvisorSaveSucceeds = true;

        var cleared = await viewModel.ClearAdvisorAsync("0123456789abcdef0123456789abcdef");

        Assert.StartsWith("Advisor off.", cleared, StringComparison.Ordinal);
        Assert.Equal((null, null), backend.AdvisorSaves[^1]);
        Assert.Null(viewModel.AdvisorLabel);
    }

    [Fact]
    public async Task The_advisor_pick_marks_the_stored_advisor_and_ends_when_the_picker_closes()
    {
        var (viewModel, backend, _, grouped) = NewViewModel();
        grouped.CurrentDefinitions = [.. Models.ProviderGroupDefinitions.All, new(Backend, Backend, "OpenAI", 99)];
        await viewModel.LoadAsync();
        viewModel.ShowSessionAdvisor(Backend, "not-listed-advisor");
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

    [Fact]
    public async Task A_new_chat_has_no_advisor_and_a_choice_waits_for_its_session()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        await viewModel.LoadAsync();
        viewModel.ShowSessionAdvisor("models.dev:openai", "previous-chat-advisor");

        // Opening a new chat shows none, whatever the previous chat used.
        viewModel.ShowSessionAdvisor(null, null);
        Assert.Null(viewModel.AdvisorLabel);

        var status = await viewModel.SelectAdvisorAsync("advisor-model", "models.dev:anthropic", sessionId: null);
        Assert.Contains("advisor-model", status, StringComparison.Ordinal);
        Assert.Empty(backend.AdvisorSaves);

        Assert.True(await viewModel.ApplyPendingAdvisorAsync("0123456789abcdef0123456789abcdef"));
        Assert.Equal("0123456789abcdef0123456789abcdef", Assert.Single(backend.AdvisorSessions));
        Assert.Equal(("models.dev:anthropic", "advisor-model"), backend.AdvisorSaves[0]);

        // Saved once; the next session does not inherit it.
        Assert.True(await viewModel.ApplyPendingAdvisorAsync("fedcba9876543210fedcba9876543210"));
        Assert.Single(backend.AdvisorSaves);
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
