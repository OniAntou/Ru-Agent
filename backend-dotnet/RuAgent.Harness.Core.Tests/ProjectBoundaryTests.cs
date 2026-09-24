using System.Xml.Linq;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void CoreProject_has_only_the_approved_protocol_dependency()
    {
        var backendRoot = FindBackendRoot();
        var projectPath = Path.Combine(
            backendRoot,
            "RuAgent.Harness.Core",
            "RuAgent.Harness.Core.csproj");

        Assert.True(File.Exists(projectPath), $"Missing Core project: {projectPath}");

        var project = XDocument.Load(projectPath);
        var projectReferences = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .ToArray();

        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal(new[] { "..\\RuAgent.Protocol\\RuAgent.Protocol.csproj" }, projectReferences);
    }

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "RuBackend"))
                && Directory.Exists(Path.Combine(current.FullName, "RuAgent.Protocol")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate backend-dotnet.");
    }
}
