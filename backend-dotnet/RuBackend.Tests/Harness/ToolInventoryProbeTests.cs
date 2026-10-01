using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The per-run tool inventory must name what the machine actually has: the
/// model reaches for what exists instead of guessing pwsh and learning via
/// executable_not_found.
/// </summary>
public sealed class ToolInventoryProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-inventory-" + Guid.NewGuid().ToString("N"));

    public ToolInventoryProbeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Probe_names_present_shells_and_tools_and_missing_rest()
    {
        File.WriteAllText(Path.Combine(_root, "powershell.exe"), "fake");
        File.WriteAllText(Path.Combine(_root, "git.exe"), "fake");

        var text = ToolInventoryProbe.Probe(new[] { _root });

        Assert.Contains("shells: powershell", text, StringComparison.Ordinal);
        Assert.Contains("tools: git", text, StringComparison.Ordinal);
        Assert.Contains("pwsh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", text.Split("missing:", StringSplitOptions.None)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_reports_none_when_the_directory_is_empty()
    {
        var text = ToolInventoryProbe.Probe(new[] { _root });

        Assert.Contains("shells: none", text, StringComparison.Ordinal);
        Assert.Contains("tools: none", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_ignores_malformed_entries_instead_of_throwing()
    {
        var text = ToolInventoryProbe.Probe(new[] { _root, "", new string('x', 300), "\0bad" });

        Assert.Contains("shells: none", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_reads_the_process_path_when_no_directories_given()
    {
        var text = ToolInventoryProbe.Probe();

        Assert.StartsWith("shells: ", text, StringComparison.Ordinal);
        Assert.Contains("; tools: ", text, StringComparison.Ordinal);
        Assert.Contains("; missing: ", text, StringComparison.Ordinal);
    }
}
