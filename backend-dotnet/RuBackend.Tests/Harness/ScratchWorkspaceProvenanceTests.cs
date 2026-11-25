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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_damaged_ownership_marker_is_repaired_before_the_next_turn(bool deletedMarker)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;

        // An earlier turn allocated this session's workspace; the agent's own file
        // tools then damaged the marker, as both runs in the report did.
        var path = scratch.Ensure(sessionId);
        await File.WriteAllTextAsync(Path.Combine(path, "work.txt"), "keep");
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        if (deletedMarker)
        {
            File.Delete(marker);
        }
        else
        {
            await File.WriteAllTextAsync(marker, "{\n  \"note\": \"rewritten inside the workspace\"\n}");
        }

        var created = await service.CreateRunAsync(Request(sessionId, string.Empty), CancellationToken.None);

        Assert.Equal(201, created.StatusCode);
        var run = Assert.IsType<CreateAgentRunResponse>(created.Body);
        Assert.Equal("scratch", run.WorkspaceKind);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(run.WorkspacePath!));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(path, "work.txt")));
        Assert.Contains(sessionId, await File.ReadAllTextAsync(marker), StringComparison.Ordinal);
    }

    /// <summary>
    /// A chat allocated before ownership records existed has only its marker. Its client
    /// echoes the scratch path, which used to bypass allocation, so the record was never
    /// written and a later damaged marker still stranded the chat.
    /// </summary>
    [Fact]
    public async Task An_echoed_path_of_a_pre_record_scratch_folder_gains_its_record()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;
        var path = scratch.PathFor(sessionId);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        await File.WriteAllTextAsync(marker, $"{{\"Version\":1,\"SessionId\":\"{sessionId}\"}}");
        Assert.False(File.Exists(scratch.RecordPathFor(sessionId)));

        var created = await service.CreateRunAsync(Request(sessionId, path), CancellationToken.None);

        Assert.Equal(201, created.StatusCode);
        Assert.Equal("scratch", Assert.IsType<CreateAgentRunResponse>(created.Body).WorkspaceKind);
        Assert.True(File.Exists(scratch.RecordPathFor(sessionId)));

        // With the record in place, a marker the agent damages afterwards is repaired.
        await File.WriteAllTextAsync(marker, "{\"note\":\"rewritten inside the workspace\"}");
        Assert.Equal(path, scratch.Ensure(sessionId));
        Assert.Contains(sessionId, await File.ReadAllTextAsync(marker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecoverable_scratch_folder_reports_what_the_user_can_do()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;
        // A direction Ru never allocated under this session: no record, foreign marker.
        var path = scratch.PathFor(sessionId);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(
            Path.Combine(path, ScratchWorkspaceStore.MarkerFileName),
            "{\"note\":\"placed by hand\"}");

        var result = await service.CreateRunAsync(Request(sessionId, string.Empty), CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        var error = Assert.IsType<AgentErrorResponse>(result.Body);
        Assert.Equal("scratch_workspace_unavailable", error.ErrorCode);
        Assert.Contains("Rename or delete that folder", error.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_busy_session_reports_the_conflict_instead_of_a_workspace_error()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (sessionId, _) = await CreateSessionAsync(fixture);
        var scratch = new ScratchWorkspaceStore(fixture.RootPath);
        var service = new AgentV1ServiceTestBuilder(fixture).WithScratchWorkspaces(scratch).Build().Creation;

        var first = await service.CreateRunAsync(Request(sessionId, string.Empty), CancellationToken.None);
        Assert.Equal(201, first.StatusCode);

        var second = await service.CreateRunAsync(Request(sessionId, string.Empty), CancellationToken.None);

        Assert.Equal(409, second.StatusCode);
        var conflict = Assert.IsType<AgentErrorResponse>(second.Body);
        Assert.Equal("workspace_busy", conflict.ErrorCode);
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
