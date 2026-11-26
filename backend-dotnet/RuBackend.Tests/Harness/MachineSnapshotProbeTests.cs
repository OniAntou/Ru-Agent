using System.Text;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class MachineSnapshotProbeTests
{
    [Fact]
    public void The_rendered_snapshot_names_platform_capacity_and_tools()
    {
        var text = MachineSnapshotProbe.Render(
            "Microsoft Windows 11 Pro",
            "X64",
            24,
            16L * 1024 * 1024 * 1024,
            ".NET 10.0.3 (X64)",
            ["powershell 5.1", "pwsh 7.4"],
            ["git 2.47", "dotnet 10.0.303 (SDK)"]);

        Assert.StartsWith("machine:", text, StringComparison.Ordinal);
        Assert.Contains("os: Microsoft Windows 11 Pro (X64)", text);
        Assert.Contains("cpu: 24 logical processor(s)", text);
        Assert.Contains("memory: 16.0 GiB visible to .NET", text);
        Assert.Contains("runtime: .NET 10.0.3 (X64)", text);
        Assert.Contains("shells: powershell 5.1, pwsh 7.4", text);
        Assert.Contains("tools: git 2.47, dotnet 10.0.303 (SDK)", text);
    }

    [Fact]
    public void Unknown_memory_and_empty_lists_are_omitted()
    {
        var text = MachineSnapshotProbe.Render("Windows", "X64", 4, 0, ".NET 10.0", [], []);

        Assert.DoesNotContain("memory:", text);
        Assert.DoesNotContain("shells:", text);
        Assert.DoesNotContain("tools:", text);
    }

    [Fact]
    public void An_oversized_snapshot_is_cut_at_its_byte_budget()
    {
        var text = MachineSnapshotProbe.Render(
            "Windows", "X64", 8, 0, new string('x', 6_000), [], []);

        Assert.True(Encoding.UTF8.GetByteCount(text) <= MachineSnapshotProbe.MaxSnapshotBytes);
        Assert.Contains("machine snapshot truncated at its byte budget", text);
    }

    [Fact]
    public void The_host_machine_describes_itself_without_running_anything()
    {
        var text = MachineSnapshotProbe.Read();

        Assert.NotNull(text);
        Assert.StartsWith("machine:", text, StringComparison.Ordinal);
        Assert.Contains("cpu: ", text);
        Assert.Contains("runtime: .NET", text);
    }
}
