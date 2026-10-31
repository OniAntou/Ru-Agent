using System.Text;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RestoreContentTests
{
    [Fact]
    public void Empty_base64_restores_an_originally_empty_file()
    {
        var ok = AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes("""{"content_base64":""}"""), out var content);

        Assert.True(ok);
        Assert.Empty(content);
    }

    [Theory]
    [InlineData("""{"content_base64":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"content_base64":"!!not-base64"}""")]
    public void Missing_null_or_invalid_content_is_unavailable(string json)
    {
        Assert.False(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void Non_empty_content_round_trips()
    {
        var payload = $$"""{"content_base64":"{{Convert.ToBase64String("abc"u8)}}"}""";

        Assert.True(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(payload), out var content));
        Assert.Equal("abc"u8.ToArray(), content);
    }

    [Theory]
    [InlineData("aa", "aa", true)]
    [InlineData("aa", "ab", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000", true)]
    [InlineData("aa", "0000000000000000000000000000000000000000000000000000000000000000", false)]
    public void Restore_target_comparison_is_exact(string current, string expected, bool matches)
    {
        Assert.Equal(matches, AgentWorkspaceService.RestoreTargetMatches(current, expected));
    }

    [Fact]
    public void Restore_rechecks_the_target_immediately_before_replacing_it()
    {
        // The first SHA check runs before journal decryption/encryption; the
        // last-moment check is what stops a concurrent edit from being overwritten.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend-dotnet", "RuBackend", "Harness", "AgentWorkspaceService.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "backend-dotnet", "RuBackend", "Harness", "AgentWorkspaceService.cs"));

        var recheck = text.IndexOf("var targetAtReplace", StringComparison.Ordinal);
        var move = text.IndexOf("File.Move(temporary, target, overwrite: true)", StringComparison.Ordinal);

        Assert.True(recheck >= 0, "the restore target must be re-read before the replace");
        Assert.True(move > recheck, "the re-check must run before File.Move");
    }
}
