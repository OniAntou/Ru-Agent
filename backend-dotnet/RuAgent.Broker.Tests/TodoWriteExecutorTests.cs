using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The todo_write executor, driven through the service the way a tool request is.
/// </summary>
/// <remarks>
/// These go through <c>ExecuteAsync</c> rather than calling the executor directly, so
/// the manifest binding, the schema validation and the policy class are all exercised
/// alongside the executor itself.
/// </remarks>
public sealed class TodoWriteExecutorTests
{
    private const string Session = "todo-session";

    [Fact]
    public async Task A_valid_list_is_stored_and_the_counts_come_back()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"one","status":"completed"},{"content":"two","status":"in_progress"}]}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal("Updated todo list: 0 pending, 1 in progress, 1 completed.", result.SafeSummary);

        var stored = service.ReadTodos(Session);
        Assert.Equal(2, stored.Count);
        Assert.Equal("one", stored[0].Content);
        Assert.Equal("in_progress", stored[1].Status);
    }

    [Fact]
    public async Task A_second_write_replaces_the_first()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"one","status":"pending"},{"content":"two","status":"pending"}]}"""),
            CancellationToken.None);
        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"three","status":"pending"}]}"""),
            CancellationToken.None);

        var stored = service.ReadTodos(Session);
        Assert.Single(stored);
        Assert.Equal("three", stored[0].Content);
    }

    /// <summary>
    /// Two kinds of rejection, and the split between them is deliberate.
    /// </summary>
    /// <remarks>
    /// A structural mistake never reaches the executor: the schema already requires the
    /// array and rejects undefined keys, so the model sees the generic
    /// <c>invalid_arguments</c>. A mistake in the list's <em>contents</em> does reach the
    /// executor and gets a specific code, because a specific code is the only thing that
    /// tells the model what to change. The schema deliberately leaves content and status
    /// unconstrained so those codes stay reachable.
    /// </remarks>
    [Theory]
    [InlineData("""{"todos":[{"content":"","status":"pending"}]}""", "todo_content_empty")]
    [InlineData("""{"todos":[{"content":"x","status":"pending"},{"content":"x","status":"pending"}]}""", "todo_content_duplicate")]
    [InlineData("""{"todos":[{"content":"a","status":"in_progress"},{"content":"b","status":"in_progress"}]}""", "todo_multiple_active")]
    [InlineData("""{"todos":[{"content":"a","status":"blocked"}]}""", "todo_status_unknown")]
    public async Task A_rejected_list_reports_its_code(string arguments, string expectedCode)
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(Envelope(arguments), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"todos":"not an array"}""")]
    [InlineData("""{"todos":[{"content":"x","status":"pending","priority":1}]}""")]
    public async Task A_structural_mistake_is_rejected_before_the_executor(string arguments)
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(Envelope(arguments), CancellationToken.None);

        Assert.NotEqual(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(service.ReadTodos(Session));
    }

    [Fact]
    public async Task A_rejected_list_leaves_the_previous_plan_intact()
    {
        // The plan on screen is the user's view of the work; a malformed write must not
        // blank it.
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"keep me","status":"pending"}]}"""),
            CancellationToken.None);

        var rejected = await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"","status":"pending"}]}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, rejected.Status);
        var stored = service.ReadTodos(Session);
        Assert.Single(stored);
        Assert.Equal("keep me", stored[0].Content);
    }

    [Fact]
    public async Task An_empty_list_clears_the_plan()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();
        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"one","status":"pending"}]}"""),
            CancellationToken.None);

        var result = await service.ExecuteAsync(
            Envelope("""{"todos":[]}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Empty(service.ReadTodos(Session));
    }

    [Fact]
    public async Task Two_sessions_keep_separate_plans()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();
        // Production always opens a second chat through its own run lease; the
        // lease is what grants that session its scopes.
        fixture.OpenSessionLease("other-session");

        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"session one task","status":"pending"}]}""", "todo-session"),
            CancellationToken.None);
        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"session two task","status":"pending"}]}""", "other-session"),
            CancellationToken.None);

        Assert.Equal("session one task", service.ReadTodos("todo-session")[0].Content);
        Assert.Equal("session two task", service.ReadTodos("other-session")[0].Content);
    }

    [Fact]
    public async Task A_session_without_its_own_lease_never_inherits_another_sessions_scopes()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"stolen","status":"pending"}]}""", "unopened-session"),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Denied, result.Status);
        Assert.Equal("scope_missing", result.ErrorCode);
        Assert.Empty(service.ReadTodos("unopened-session"));
    }

    [Fact]
    public async Task Removing_a_session_drops_its_plan()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();
        await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"one","status":"pending"}]}"""),
            CancellationToken.None);

        await service.RemoveSessionAsync(Session);

        Assert.Empty(service.ReadTodos(Session));
    }

    [Fact]
    public Task A_row_carrying_an_unknown_key_is_rejected_by_the_schema()
    {
        // The schema is strict about keys, so a field the model invented is a visible
        // rejection rather than a silently dropped value.
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        return AssertRejectedAsync(service);
    }

    private static async Task AssertRejectedAsync(ToolExecutionService service)
    {
        var result = await service.ExecuteAsync(
            Envelope("""{"todos":[{"content":"x","status":"pending","priority":1}]}"""),
            CancellationToken.None);

        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(service.ReadTodos(Session));
    }

    private static AgentEnvelope Envelope(string arguments, string sessionId = Session) =>
        new(
            1,
            $"req_{Guid.NewGuid():N}",
            AgentMessageTypes.ToolRequest,
            sessionId,
            null,
            30_000,
            new JsonObject
            {
                ["tool_id"] = "todo_write",
                ["tool_version"] = "1.0.0",
                ["arguments"] = JsonNode.Parse(arguments),
            });

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, BrokerSession session)
        {
            Root = root;
            Session = session;
        }

        public string Root { get; }

        public BrokerSession Session { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-todo-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            return new Fixture(
                root,
                BrokerSession.CreateForHost(sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public ToolExecutionService CreateService()
        {
            Session.Open(
                Session_,
                new HashSet<string>(
                    [RuAgent.Broker.Policy.ToolScope.SystemRead],
                    StringComparer.Ordinal));
            return new ToolExecutionService(Session, Root);
        }

        public void OpenSessionLease(string sessionId)
        {
            Session.ConfigureAdvertisedCapabilities(["read"]);
            var snapshot = LeaseTestSnapshots.StandardEmpty();
            var lease = BrokerRunLease.CreateValidated(
                "run_" + Guid.NewGuid().ToString("N"),
                sessionId,
                LeaseTestSnapshots.WorkspaceId,
                Root,
                LeaseTestSnapshots.VolumeSerial,
                LeaseTestSnapshots.RootFileId,
                LeaseTestSnapshots.DisplayHash,
                snapshot.Preset,
                snapshot.Version,
                RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
                DateTimeOffset.UtcNow.AddMinutes(5),
                snapshot,
                advertisedCapabilities: ["read"]);
            Assert.True(Session.TryOpenRunLease(lease, out var error), error);
        }

        private const string Session_ = "todo-session";

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
