using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>ADR 0047, amended: each session keeps its own advisor, and a new one has none.</summary>
public sealed class SessionAdvisorStoreTests
{
    [Fact]
    public async Task A_session_advisor_is_set_read_through_its_runs_and_cleared()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var session = request.Run.SessionId.Value;

        Assert.Null(await store.GetSessionAdvisorAsync(session, default));
        Assert.Null(await store.GetRunSessionAdvisorAsync(request.Run.RunId, default));

        await store.SetSessionAdvisorAsync(session, new RunProviderIntent("Anthropic", "claude-opus-5-5"), default);
        await store.SetSessionAdvisorAsync(session, new RunProviderIntent("OpenAI", "gpt-5"), default);

        Assert.Equal(new RunProviderIntent("OpenAI", "gpt-5"), await store.GetSessionAdvisorAsync(session, default));
        Assert.Equal(new RunProviderIntent("OpenAI", "gpt-5"), await store.GetRunSessionAdvisorAsync(request.Run.RunId, default));
        Assert.Null(await store.GetSessionAdvisorAsync(HarnessStoreTestFixture.Session(2).Value, default));

        await store.SetSessionAdvisorAsync(session, null, default);
        Assert.Null(await store.GetSessionAdvisorAsync(session, default));
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_advisor()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        var store = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var session = request.Run.SessionId.Value;
        await store.SetSessionAdvisorAsync(session, new RunProviderIntent("Anthropic", "claude-opus-5-5"), default);

        await new HarnessSessionArtifactStore(fixture.Options).CloseAndDeleteAsync(
            session, HarnessStoreTestFixture.Timestamp.AddMinutes(1), CancellationToken.None);

        Assert.Null(await store.GetSessionAdvisorAsync(session, default));
    }
}
