using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ComposerSlashCommandsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "ru-gui-slash-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ru-gui-slash-home-" + Guid.NewGuid().ToString("N"));

    public ComposerSlashCommandsTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_home);
    }

    [Fact]
    public void A_typed_custom_command_expands_to_its_prompt_with_arguments()
    {
        Write(_workspace, "review", "---\ndescription: Review a file\n---\nReview $ARGUMENTS carefully.");

        Assert.Equal("Review src/a.cs carefully.", ComposerSlashCommands.Expand("/review src/a.cs", _workspace, _home));
        Assert.Equal("Review  carefully.", ComposerSlashCommands.Expand("  /review", _workspace, _home));
        // Arguments may continue on the next line.
        Assert.Equal("Review line two carefully.", ComposerSlashCommands.Expand("/review\nline two", _workspace, _home));
    }

    [Theory]
    [InlineData("review src/a.cs")]
    [InlineData("/unknown now")]
    [InlineData("/etc/hosts is a path, not a command")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_text_and_unknown_commands_are_sent_unchanged(string? text)
    {
        Write(_workspace, "review", "Review $ARGUMENTS");

        Assert.Null(ComposerSlashCommands.Expand(text, _workspace, _home));
    }

    [Fact]
    public void A_template_can_never_replace_a_built_in_command()
    {
        Write(_workspace, "skills", "hijacked");
        Write(_workspace, "skill", "hijacked");

        Assert.Null(ComposerSlashCommands.Expand("/skills", _workspace, _home));
        Assert.Null(ComposerSlashCommands.Expand("/skill reviewer check this", _workspace, _home));
        var skills = Assert.Single(ComposerSlashCommands.Suggest("/sk", _workspace, _home));
        Assert.True(skills.IsBuiltIn);
        Assert.Equal("/skills", skills.Command);
    }

    [Fact]
    public void The_menu_lists_workspace_then_personal_templates_then_built_ins_and_closes_after_the_token()
    {
        Write(_workspace, "review", "---\ndescription: \"Review   a\n file\"\n---\nbody");
        Write(_home, "refactor", "body");
        Write(_home, "explain", "body");

        var all = ComposerSlashCommands.Suggest("/", _workspace, _home);
        Assert.Equal(["/review", "/explain", "/refactor", "/skills"], all.Select(row => row.Command));
        Assert.Equal(["/review", "/refactor"], ComposerSlashCommands.Suggest("/re", _workspace, _home).Select(row => row.Command));
        Assert.Empty(ComposerSlashCommands.Suggest("/review ", _workspace, _home));
        Assert.Empty(ComposerSlashCommands.Suggest("review", _workspace, _home));
        Assert.Empty(ComposerSlashCommands.Suggest("/Review", _workspace, _home));
        Assert.False(ComposerSlashCommands.IsTypingCommand("hello /re"));
    }

    [Fact]
    public void The_menu_is_bounded_and_a_description_is_one_short_line()
    {
        for (var index = 0; index < 20; index++) Write(_workspace, $"cmd{index:00}", "body");
        Write(_workspace, "aaa", "---\ndescription: " + new string('d', 400) + "\n---\nbody");

        var rows = ComposerSlashCommands.Suggest("/", _workspace, _home);

        Assert.Equal(ComposerSlashCommands.MaxSuggestions, rows.Count);
        Assert.Equal(120, rows[0].Description.Length);
        Assert.DoesNotContain('\n', rows[0].Description);
    }

    [Fact]
    public void A_row_says_where_it_comes_from_what_was_typed_and_whether_it_takes_arguments()
    {
        Write(_workspace, "review", "Review $ARGUMENTS carefully.");
        Write(_home, "review", "shadowed personal copy");
        Write(_home, "refactor", "Refactor the open file.");

        var rows = ComposerSlashCommands.Suggest("/re", _workspace, _home);

        var review = rows[0];
        Assert.Equal(ComposerSlashSource.Workspace, review.Source);
        Assert.True(review.TakesArguments);
        Assert.Equal("/re", review.MatchedText);
        Assert.Equal("view", review.RemainingText);
        var refactor = rows[1];
        Assert.Equal(ComposerSlashSource.Personal, refactor.Source);
        Assert.False(refactor.TakesArguments);
        Assert.False(refactor.IsBuiltIn);
        Assert.NotEqual(refactor.Glyph, ComposerSlashCommands.Suggest("/sk", _workspace, _home)[0].Glyph);
    }

    [Fact]
    public void Groups_follow_menu_order_and_a_single_source_is_one_group()
    {
        Write(_workspace, "review", "body");
        Write(_home, "explain", "body");

        var groups = ComposerSlashCommands.Group(ComposerSlashCommands.Suggest("/", _workspace, _home));
        Assert.Equal(["Workspace", "Personal", "Ru"], groups.Select(group => group.Label));
        Assert.Equal(["/review", "/explain", "/skills"], groups.SelectMany(group => group).Select(row => row.Command));

        Assert.Single(ComposerSlashCommands.Group(ComposerSlashCommands.Suggest("/sk", _workspace, _home)));
    }

    private static void Write(string root, string name, string content)
    {
        var directory = Path.Combine(root, ".ru", "commands");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".md"), content);
    }

    public void Dispose()
    {
        Directory.Delete(_workspace, recursive: true);
        Directory.Delete(_home, recursive: true);
    }
}
