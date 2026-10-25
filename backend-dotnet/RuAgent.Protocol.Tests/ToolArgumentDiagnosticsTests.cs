using System.Text.Json;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class ToolArgumentDiagnosticsTests
{
    [Theory]
    [InlineData("write_file", "{\"path\":\"file\",\"content\":42}", "content")]
    [InlineData("write_file", "{\"path\":42,\"content\":\"private content\"}", "path")]
    [InlineData("powershell", "{\"script\":42}", "script")]
    [InlineData("run_process", "{\"executable\":\"node\",\"arguments\":\"private content\"}", "arguments")]
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
        using var json = JsonDocument.Parse("""{"purpose":"\u0076erification","script":"dotnet test"}""");
        Assert.Null(ToolArgumentDiagnostics.Find("powershell", "1.0.0", json.RootElement));
    }
}
