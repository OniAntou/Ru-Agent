using System.Text.Json;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class ToolArgumentDiagnosticsTests
{
    [Theory]
    [InlineData("write", "{\"op\":\"write\\\" \",\"path\":\"file\",\"content\":\"private content\"}", "op")]
    [InlineData("write", "{\"path\":\"file\",\"content\":\"private content\"}", "op")]
    [InlineData("bash", "{\"mode\":\"shell\\\" \",\"script\":\"private content\"}", "mode")]
    [InlineData("bash", "{\"mode\":\"run\",\"arguments\":\"private content\"}", "arguments")]
    public void Feedback_names_the_schema_field_without_echoing_scripts_or_file_content(string tool, string args, string field)
    {
        using var json = JsonDocument.Parse(args);
        var issue = Assert.IsType<ToolArgumentIssue>(ToolArgumentDiagnostics.Find(tool, "1.0.0", json.RootElement));
        Assert.Equal(field, issue.Field);
        Assert.DoesNotContain("private content", issue.Hint);
        Assert.Contains(field, issue.Hint);
    }

    [Fact]
    public void A_valid_escaped_enum_is_not_a_malformed_argument()
    {
        using var json = JsonDocument.Parse("""{"mode":"\u0073hell","script":"echo ok"}""");
        Assert.Null(ToolArgumentDiagnostics.Find("bash", "1.0.0", json.RootElement));
    }
}
