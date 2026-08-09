using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendLaunchPlanTests
{
    [Fact]
    public void FastApiModeKeepsThePublicPortAtEightThousand()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: false);

        var plan = BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            BackendHostMode.FastApi,
            workerPort: 8001);
        var child = Assert.Single(plan.Children);

        Assert.Equal(BackendHostMode.FastApi, plan.Mode);
        Assert.Equal(8000, plan.PublicPort);
        Assert.Null(plan.WorkerPort);
        Assert.Equal("fastapi-worker", child.Role);
        Assert.Equal(project.PythonPath, child.FileName);
        Assert.Equal(
            "-m mini_gpt.server --host 127.0.0.1 --port 8000",
            child.Arguments);
        Assert.Equal("secret-token", child.Environment["RU_DESKTOP_TOKEN"]);
    }

    [Fact]
    public void AspNetCoreModeStartsWorkerBeforePublicEdge()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);

        var plan = BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            BackendHostMode.AspNetCore,
            workerPort: 8001);

        Assert.Equal(BackendHostMode.AspNetCore, plan.Mode);
        Assert.Equal(8000, plan.PublicPort);
        Assert.Equal(8001, plan.WorkerPort);
        Assert.Equal(new[] { "fastapi-worker", "aspnetcore-edge" }, plan.Children.Select(child => child.Role));
        Assert.Equal(
            "-m mini_gpt.server --host 127.0.0.1 --port 8001",
            plan.Children[0].Arguments);
        Assert.Equal(project.EdgePath, plan.Children[1].FileName);
        Assert.Equal("http://127.0.0.1:8000", plan.Children[1].Environment["RuBackend__BindUrl"]);
        Assert.Equal("http://127.0.0.1:8001", plan.Children[1].Environment["RuBackend__UpstreamUrl"]);
        Assert.Equal("secret-token", plan.Children[0].Environment["RU_DESKTOP_TOKEN"]);
        Assert.Equal("secret-token", plan.Children[1].Environment["RU_DESKTOP_TOKEN"]);
    }

    [Fact]
    public void MissingPublishedEdgeIsRejectedBeforeAChildCanStart()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: false);

        var error = Assert.Throws<FileNotFoundException>(() => BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            BackendHostMode.AspNetCore,
            workerPort: 8001));

        Assert.Contains("RuBackend.exe", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fastapi", BackendHostMode.FastApi)]
    [InlineData("aspnetcore", BackendHostMode.AspNetCore)]
    [InlineData("", BackendHostMode.AspNetCore)]
    public void HostModeParsingIsStrict(string raw, BackendHostMode expected)
    {
        Assert.Equal(expected, BackendHostModeParser.Parse(raw));
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string root, string pythonPath, string edgePath)
        {
            Root = root;
            PythonPath = pythonPath;
            EdgePath = edgePath;
        }

        public string Root { get; }
        public string PythonPath { get; }
        public string EdgePath { get; }

        public static TemporaryProject Create(bool includePublishedEdge)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-backend-plan-" + Guid.NewGuid().ToString("N"));
            var pythonPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
            var edgePath = Path.Combine(
                root,
                "backend-dotnet",
                "RuBackend",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuBackend.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            File.WriteAllText(pythonPath, "test");
            if (includePublishedEdge)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(edgePath)!);
                File.WriteAllText(edgePath, "test");
            }

            return new TemporaryProject(root, pythonPath, edgePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
