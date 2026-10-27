using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SkillMetadataParserTests
{
    [Fact]
    public void Reads_folded_and_literal_yaml_scalars_and_list_requirements()
    {
        var folded = SkillMetadataParser.Parse("---\nname: shown-name\ndescription: >\n  Reviews source\n  for defects.\nrequires:\n  - python\n  - node\ncompatibility: |\n  Works on Windows\n  and Linux\n---\nBody", "canonical-id");
        Assert.Equal("canonical-id", folded.Id);
        Assert.Equal("shown-name", folded.Name);
        Assert.Contains("Reviews source", folded.Description);
        Assert.Equal(new[] { "python", "node" }, folded.Requires);
        Assert.Contains("Works on Windows", folded.Compatibility);
    }

    [Fact]
    public void Manual_only_metadata_is_explicit_and_unsupported_fields_are_reported()
    {
        var parsed = SkillMetadataParser.Parse("---\nname: Verify\ndescription: 'A: B'\ndisable-model-invocation: true\nuser-invocable: false\nunknown: value\n---\nBody", "verify");
        Assert.Equal("A: B", parsed.Description);
        Assert.False(parsed.ModelInvocable);
        Assert.False(parsed.UserInvocable);
        Assert.Contains(parsed.Diagnostics, item => item.StartsWith("unsupported_field", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_yaml_aliases_and_excessive_nesting()
    {
        var alias = SkillMetadataParser.Parse("---\nname: verify\ndescription: &summary Shared\ncompatibility: *summary\n---\nBody", "verify");
        Assert.False(alias.ModelInvocable);
        Assert.Contains("invalid_frontmatter", alias.Diagnostics);

        var nested = "---\nname: verify\nmetadata: " + string.Concat(Enumerable.Repeat("{a: ", 20)) + "x" + new string('}', 20) + "\n---\nBody";
        var deep = SkillMetadataParser.Parse(nested, "verify");
        Assert.False(deep.ModelInvocable);
        Assert.Contains("invalid_frontmatter", deep.Diagnostics);
    }

    [Theory]
    [InlineData("verify", true)]
    [InlineData("friendly-skill", true)]
    [InlineData("pdf.analyzer", true)]
    [InlineData("Unsafe Name", false)]
    [InlineData("../bad", false)]
    public void Validates_canonical_folder_ids(string id, bool expected) => Assert.Equal(expected, SkillIdentity.IsValid(id));
}
