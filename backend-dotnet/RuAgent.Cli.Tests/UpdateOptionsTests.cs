using RuAgent.Updates;
using Xunit;
namespace RuAgent.Cli.Tests;
public sealed class UpdateOptionsTests
{
    [Fact] public void Defaults_are_checkable_without_implicit_confirmation()
    { var options = UpdateCliOptions.Parse([])!; Assert.False(options.Confirmed); Assert.Null(options.Channel); }
    [Fact] public void Preview_is_an_explicit_choice()
    { var options = UpdateCliOptions.Parse(["--channel","preview","--check"])!; Assert.Equal(UpdateChannel.Preview,options.Channel); Assert.True(options.CheckOnly); Assert.False(options.Confirmed); }
    [Theory]
    [InlineData("--channel", "nightly")]
    [InlineData("--yes", "--yes")]
    [InlineData("--check", "--unknown")]
    public void Rejects_unsupported_or_duplicate_options(string a,string b) => Assert.Null(UpdateCliOptions.Parse([a,b]));
}
