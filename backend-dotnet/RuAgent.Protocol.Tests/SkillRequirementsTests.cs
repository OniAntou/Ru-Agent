using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SkillRequirementsTests
{
    [Fact]
    public void Python_requirement_fails_with_python_unavailable_when_the_probe_is_false()
    {
        var failure = SkillRequirements.FindUnmet(["python"], () => false);

        Assert.NotNull(failure);
        Assert.Equal("python_unavailable", failure.Code);
    }

    [Fact]
    public void Python_requirement_met_by_the_probe_has_no_failure()
    {
        Assert.Null(SkillRequirements.FindUnmet(["PYTHON"], () => true));
    }

    [Fact]
    public void Unknown_token_fails_closed_naming_the_token()
    {
        var failure = SkillRequirements.FindUnmet(["node"], () => true);

        Assert.NotNull(failure);
        Assert.Equal("unsupported_skill_requirement", failure.Code);
        Assert.Contains("node", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_strips_brackets_quotes_and_duplicates_and_sanitizes_tokens()
    {
        Assert.Equal(
            new[] { "python", "co?de", "quoted" },
            SkillRequirements.Parse("[python, PYTHON, \"co\"de\", 'quoted']"));
    }

    [Fact]
    public void Parse_caps_the_token_count_and_length()
    {
        var tokens = Enumerable.Range(0, 9).Select(index => "t" + index.ToString());
        var longToken = new string('a', 80);

        Assert.Equal(8, SkillRequirements.Parse(string.Join(',', tokens)).Count);
        Assert.Equal(64, SkillRequirements.Parse(longToken).Single().Length);
    }
}
