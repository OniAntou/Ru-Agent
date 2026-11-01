using RuAgent.Protocol.Harness;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness.Workspace;
using System.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class ScratchWorkspaceProvenanceTests
{
    [Fact]
    public async Task Explicit_unmarked_scratch_child_is_a_project_workspace_and_requires_trust()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        const string otherSessionId = "88888888888888888888888888888888";
        var path = scratch.PathFor(otherSessionId);
        Directory.CreateDirectory(path);
        var userFile = Path.Combine(path, "user-data.txt");
        await File.WriteAllTextAsync(userFile, "keep");
        Assert.False(File.Exists(Path.Combine(path, ScratchWorkspaceStore.MarkerFileName)));

        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;
        var result = await service.CreateRunAsync(Request(sessionId, path), CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        var created = Assert.IsType<CreateAgentRunResponse>(result.Body);
        Assert.Equal("project", created.WorkspaceKind);
        Assert.Equal("waiting_workspace_trust", created.Status);
        Assert.True(File.Exists(userFile));
        Assert.False(File.Exists(Path.Combine(path, ScratchWorkspaceStore.MarkerFileName)));
    }

    [Fact]
    public async Task A_marker_for_another_session_does_not_prove_scratch_ownership()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        const string otherSessionId = "99999999999999999999999999999999";
        var path = scratch.PathFor(otherSessionId);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        await File.WriteAllTextAsync(marker,
            "{\"Version\":1,\"SessionId\":\"" + otherSessionId + "\"}");

        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;
        var result = await service.CreateRunAsync(Request(sessionId, path), CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        var created = Assert.IsType<CreateAgentRunResponse>(result.Body);
        Assert.Equal("project", created.WorkspaceKind);
        Assert.Equal("waiting_workspace_trust", created.Status);
    }

    [Fact]
    public async Task Junction_under_scratch_root_is_not_classified_as_an_owned_scratch_workspace()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var outside = Path.Combine(fixture.RootPath, "outside-user-folder");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, ScratchWorkspaceStore.MarkerFileName);
        await File.WriteAllTextAsync(marker, "{\"Version\":1,\"SessionId\":\"" + sessionId + "\"}");
        Directory.CreateDirectory(scratch.Root);
        var link = scratch.PathFor(sessionId);

        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }))
        {
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(15_000), "junction creation timed out");
            Assert.Equal(0, process.ExitCode);
        }

        try
        {
            var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;
            var result = await service.CreateRunAsync(Request(sessionId, link), CancellationToken.None);

            Assert.Equal(201, result.StatusCode);
            var created = Assert.IsType<CreateAgentRunResponse>(result.Body);
            Assert.Equal("project", created.WorkspaceKind);
            Assert.Equal("waiting_workspace_trust", created.Status);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public async Task Empty_workspace_path_allocates_a_marked_scratch_workspace_for_the_session()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;

        var result = await service.CreateRunAsync(Request(sessionId, string.Empty), CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        var created = Assert.IsType<CreateAgentRunResponse>(result.Body);
        Assert.Equal("scratch", created.WorkspaceKind);
        Assert.Equal(Path.GetFullPath(scratch.PathFor(sessionId)), Path.GetFullPath(created.WorkspacePath!));
        var marker = await File.ReadAllTextAsync(Path.Combine(created.WorkspacePath!, ScratchWorkspaceStore.MarkerFileName));
        Assert.Contains(sessionId, marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_session_does_not_allocate_a_scratch_workspace()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        const string missingSessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;

        var result = await service.CreateRunAsync(Request(missingSessionId, string.Empty), CancellationToken.None);

        Assert.Equal(404, result.StatusCode);
        Assert.False(scratch.Exists(missingSessionId));
    }

    private static CreateAgentRunRequest Request(string sessionId, string workspacePath) => new(
        sessionId,
        workspacePath,
        "scratch provenance regression",
        "standard",
        null,
        "scratch-provenance-" + Guid.NewGuid().ToString("N"));

    private static async Task<(string SessionId, SessionStore Sessions)> CreateSessionAsync(
        BackendHarnessTestFixture fixture)
    {
        var paths = new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log"));
        var sessions = new SessionStore(paths, TimeProvider.System);
        var sessionId = await sessions.CreateAsync(CancellationToken.None);
        return (sessionId!, sessions);
    }
}
