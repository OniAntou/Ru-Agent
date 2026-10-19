using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileSearchGeneratedDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-search-generated-" + Guid.NewGuid().ToString("N"));

    public FileSearchGeneratedDirectoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Recursive_search_skips_vcs_dependency_and_project_build_output()
    {
        Write("src/app.txt");
        Write("node_modules/dep/index.txt");
        Write(".git/notes.txt");
        Write("proj/App.csproj");
        Write("proj/obj/generated.txt");
        Write("proj/bin/out.txt");

        var paths = await SearchPathsAsync(".");

        Assert.Contains("src/app.txt", paths);
        Assert.Contains("proj/App.csproj", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("node_modules/") || path.StartsWith(".git/"));
        Assert.DoesNotContain(paths, path => path.StartsWith("proj/obj/") || path.StartsWith("proj/bin/"));
    }

    [Fact]
    public async Task Source_directory_named_bin_without_a_project_file_is_still_searched()
    {
        Write("tools/bin/run.txt");

        Assert.Contains("tools/bin/run.txt", await SearchPathsAsync("."));
    }

    [Fact]
    public async Task Naming_a_generated_directory_as_the_search_path_searches_it()
    {
        Write("node_modules/dep/index.txt");

        Assert.Contains("node_modules/dep/index.txt", await SearchPathsAsync("node_modules"));
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "needle");
    }

    private async Task<List<string>> SearchPathsAsync(string path)
    {
        var result = await new FileSearchAdapter(_root).ExecuteAsync(
            new JsonObject { ["query"] = "needle", ["path"] = path }, CancellationToken.None);
        return result["results"]!.AsArray().Select(match => match!["path"]!.GetValue<string>()).ToList();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
