using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class WorkspaceTrustPromptPolicyTests
{
    [Fact]
    public void The_prompt_names_the_folder_being_trusted()
    {
        var prompt = WorkspaceTrustPromptPolicy.For(isReTrust: false, @"D:\work\ru-demo");

        Assert.Equal(@"D:\work\ru-demo", prompt.WorkspaceText);
        Assert.Null(prompt.ScopeWarning);
        Assert.Equal("Approve", prompt.PrimaryButtonText);
    }

    [Fact]
    public void An_unknown_folder_is_said_to_be_unknown_rather_than_guessed()
    {
        var prompt = WorkspaceTrustPromptPolicy.For(isReTrust: true, workspacePath: null);

        Assert.Equal(WorkspaceTrustPromptPolicy.UnknownWorkspaceText, prompt.WorkspaceText);
        Assert.Equal("Trust again", prompt.PrimaryButtonText);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"c:\")]
    public void A_drive_root_is_called_out(string path)
    {
        var warning = WorkspaceTrustPromptPolicy.ScopeWarningFor(path);

        Assert.NotNull(warning);
        Assert.Contains("root of drive", warning, StringComparison.Ordinal);
        // "drive D:." rather than "drive D:\." — the separator is not part of the drive's name.
        Assert.Contains($"drive {path[..2]}.", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_user_profile_is_called_out()
    {
        Assert.Contains(
            "user folder",
            WorkspaceTrustPromptPolicy.ScopeWarningFor(@"C:\Users\someone\", userProfile: @"C:\Users\someone"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"D:\LLM")]
    [InlineData(@"C:\Users\someone\projects\app")]
    [InlineData("")]
    public void An_ordinary_project_folder_has_no_warning(string path)
    {
        Assert.Null(WorkspaceTrustPromptPolicy.ScopeWarningFor(path, userProfile: @"C:\Users\someone"));
    }
}
