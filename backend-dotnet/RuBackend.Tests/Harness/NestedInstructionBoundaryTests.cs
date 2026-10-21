using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class NestedInstructionBoundaryTests
{
    [Fact]
    public void Nested_instructions_do_not_bypass_catalog_protected_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-nested-boundary-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "component", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "SHOULD_NOT_REACH_CONTEXT");
        var before = Environment.GetEnvironmentVariable("RU_PROTECTED_PATHS");
        try
        {
            Environment.SetEnvironmentVariable("RU_PROTECTED_PATHS", string.IsNullOrWhiteSpace(before) ? file : before + ";" + file);
            var result = new RuInstructionLoader(localApplicationDataRoot: root).Load(root, ["component/source.cs"]);
            Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("SHOULD_NOT_REACH_CONTEXT"));
            Assert.Contains(result.Skips, skip => skip.Reason == "protected_instruction");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_PROTECTED_PATHS", before);
            Directory.Delete(root, recursive: true);
        }
    }
}
