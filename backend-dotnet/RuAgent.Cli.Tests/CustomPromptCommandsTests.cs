using RuAgent.Cli;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class CustomPromptCommandsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "ru-custom-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ru-custom-home-" + Guid.NewGuid().ToString("N"));

    public CustomPromptCommandsTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_home);
    }

    [Fact]
    public void Workspace_template_shadows_the_user_template_and_expands_arguments()
    {
        Write(_workspace, "review", "---\ndescription: \"Review a file\"\n---\nReview $ARGUMENTS carefully.");
        Write(_home, "review", "user version");

        var command = CustomPromptCommands.Find("/review", _workspace, _home);

        Assert.NotNull(command);
        Assert.Equal("Review a file", command!.Description);
        Assert.Equal("Review src/a.cs carefully.", CustomPromptCommands.Expand(command, " src/a.cs "));
    }

    [Fact]
    public void Arguments_are_appended_when_the_template_has_no_placeholder()
    {
        Write(_home, "explain", "Explain the code.\r\n");

        var command = CustomPromptCommands.Find("/explain", _workspace, _home)!;

        Assert.Equal("Explain the code.\n\nfoo", CustomPromptCommands.Expand(command, "foo"));
        Assert.Equal("Explain the code.", CustomPromptCommands.Expand(command, null));
    }

    [Theory]
    [InlineData("/../secret")]
    [InlineData("/Bad")]
    [InlineData("/a/b")]
    [InlineData("review")]
    [InlineData("/")]
    public void Invalid_names_never_touch_the_file_system(string token)
    {
        Write(_workspace, "secret", "x");

        Assert.Null(CustomPromptCommands.Find(token, _workspace, _home));
    }

    [Fact]
    public void Empty_oversized_and_invalid_utf8_templates_are_ignored()
    {
        Write(_workspace, "empty", "  \n");
        Write(_workspace, "big", new string('a', CustomPromptCommands.MaxTemplateBytes + 1));
        var directory = Path.Combine(_workspace, ".ru", "commands");
        File.WriteAllBytes(Path.Combine(directory, "bad.md"), [0xff, 0xfe, 0xfd]);

        Assert.Null(CustomPromptCommands.Find("/empty", _workspace, _home));
        Assert.Null(CustomPromptCommands.Find("/big", _workspace, _home));
        Assert.Null(CustomPromptCommands.Find("/bad", _workspace, _home));
        Assert.Empty(CustomPromptCommands.List(_workspace, _home));
    }

    [Fact]
    public void List_merges_workspace_and_user_names_in_order()
    {
        Write(_workspace, "b", "b");
        Write(_home, "a", "a");
        Write(_home, "b", "b2");

        Assert.Equal(["a", "b"], CustomPromptCommands.List(_workspace, _home));
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
