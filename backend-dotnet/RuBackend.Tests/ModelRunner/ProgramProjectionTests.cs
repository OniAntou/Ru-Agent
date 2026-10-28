using System.Text.Json;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class ProgramProjectionTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(100_000)]
    public void OriginalTraceNeverEntersProviderContextEvenWhenItFits(int budget)
    {
        const string source = """{"program_id":"trace","status":"succeeded","result":{"answer":42},"tool_count":1,"nested_receipts":[{"output":"large-private-trace"}]}""";
        var projected = StructuredToolOutputProjector.Project("run_code", null, source, budget,
            ToolOutputProjectionUnit.Utf8Bytes, (_, _) => throw new Exception("fits"));
        using var json = JsonDocument.Parse(projected);
        Assert.False(json.RootElement.TryGetProperty("nested_receipts", out _));
        Assert.Equal(42, json.RootElement.GetProperty("result").GetProperty("answer").GetInt32());
        Assert.DoesNotContain("large-private-trace", projected, StringComparison.Ordinal);
    }
}
