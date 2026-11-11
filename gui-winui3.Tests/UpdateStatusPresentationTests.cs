using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UpdateStatusPresentationTests
{
    [Theory]
    [InlineData(UpdateUiState.Idle, null, "Not checked yet")]
    [InlineData(UpdateUiState.Checking, null, "Checking for updates…")]
    [InlineData(UpdateUiState.Available, "0.9.5", "Ru 0.9.5 is available")]
    [InlineData(UpdateUiState.Available, null, "An update is available")]
    [InlineData(UpdateUiState.UpToDate, null, "Ru is up to date")]
    [InlineData(UpdateUiState.Failed, null, "Couldn't check for updates")]
    [InlineData(UpdateUiState.Downloading, "0.9.5", "Downloading Ru 0.9.5")]
    [InlineData(UpdateUiState.Downloading, " ", "Downloading the update")]
    [InlineData(UpdateUiState.Installing, null, "Installing the update")]
    [InlineData(UpdateUiState.Cancelled, null, "Update cancelled")]
    [InlineData(UpdateUiState.SourceBuild, null, "Source build")]
    [InlineData(UpdateUiState.Unavailable, null, "Updates unavailable")]
    public void Each_state_has_a_plain_headline(UpdateUiState state, string? version, string expected) =>
        Assert.Equal(expected, UpdateStatusPresentation.Headline(state, version));

    [Fact]
    public void Every_state_has_a_headline()
    {
        foreach (var state in Enum.GetValues<UpdateUiState>())
            Assert.False(string.IsNullOrWhiteSpace(UpdateStatusPresentation.Headline(state, "1.0.0")), state.ToString());
    }

    [Theory]
    [InlineData(UpdateUiState.Idle, "Check for updates")]
    [InlineData(UpdateUiState.UpToDate, "Check for updates")]
    [InlineData(UpdateUiState.Available, "Check again")]
    [InlineData(UpdateUiState.Failed, "Try again")]
    public void The_check_button_doubles_as_the_retry(UpdateUiState state, string expected) =>
        Assert.Equal(expected, UpdateStatusPresentation.CheckLabel(state));

    [Fact]
    public void Install_exists_only_with_a_release_to_install()
    {
        foreach (var state in Enum.GetValues<UpdateUiState>())
        {
            bool expected = state is UpdateUiState.Available or UpdateUiState.Cancelled;
            Assert.Equal(expected, UpdateStatusPresentation.ShowInstall(state));
        }
    }

    [Fact]
    public void Nothing_can_be_checked_while_an_update_moves_or_on_a_source_build()
    {
        foreach (var state in new[]
                 {
                     UpdateUiState.Downloading, UpdateUiState.Installing, UpdateUiState.SourceBuild, UpdateUiState.Unavailable,
                 })
            Assert.False(UpdateStatusPresentation.ShowCheck(state), state.ToString());
        foreach (var state in new[] { UpdateUiState.Idle, UpdateUiState.Available, UpdateUiState.UpToDate, UpdateUiState.Failed })
            Assert.True(UpdateStatusPresentation.ShowCheck(state), state.ToString());
    }

    [Fact]
    public void Only_a_download_can_be_cancelled_and_progress_follows_the_install_path()
    {
        Assert.True(UpdateStatusPresentation.ShowCancel(UpdateUiState.Downloading));
        Assert.False(UpdateStatusPresentation.ShowCancel(UpdateUiState.Installing));
        Assert.True(UpdateStatusPresentation.ShowProgress(UpdateUiState.Downloading));
        Assert.True(UpdateStatusPresentation.ShowProgress(UpdateUiState.Installing));
        Assert.False(UpdateStatusPresentation.ShowProgress(UpdateUiState.Available));
    }
}
