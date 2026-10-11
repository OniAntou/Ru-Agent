using System.Text.Json;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class FileSearchProjectionQualityTests
{
    [Fact]
    public void Incomplete_scan_without_a_known_next_page_does_not_invent_a_cursor()
    {
        const string source = """{"results":[],"count":0,"truncated":true,"scan_incomplete":true,"offset":0,"next_offset":null}""";
        using var arguments = JsonDocument.Parse("""{"query":"needle"}""");
        var output = StructuredToolOutputProjector.Project("grep", arguments.RootElement, source, 2000,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        Assert.DoesNotContain("projection_next_offset", output);
        using var parsed = JsonDocument.Parse(output);
        Assert.True(parsed.RootElement.GetProperty("scan_incomplete").GetBoolean());
    }
}
