using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class UserImageHistoryTests
{
    private const string Image = "data:image/png;base64,aW1hZ2U=";

    [Theory]
    [InlineData("Can you read this?", true)]
    [InlineData("", true)]
    [InlineData("Can you read this?", false)]
    public async Task Follow_up_keeps_attachment_facts_only_from_visible_history(string caption, bool visible)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var previous = await fixture.SeedRunAsync(payloads:
        [new HarnessPayloadIntent(new PayloadId("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            HarnessPayloadKind.UserImage, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, Encoding.UTF8.GetBytes(Image))]);
        var ended = BackendHarnessTestFixture.Timestamp.AddSeconds(1);
        var stopped = new AgentRun(previous.Run.RunId, previous.Run.SessionId, previous.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Cancelled), 1, 2, 1, 0, 0,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, previous.Run.CreatedAt, ended, terminalAt: ended);
        await fixture.EventStore.CommitAsync(new HarnessCommit(previous.Run.RunId, 0, stopped,
            [new HarnessEventIntent(new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), HarnessEventKind.RunStateChanged)],
            [], []), default);
        var next = new AgentRun(new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), previous.Run.SessionId,
            previous.Run.WorkspaceId, new HarnessRunStatus(HarnessRunState.Created), 0, 1, 1, 0, 0,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, ended, ended);
        var admitted = await fixture.EventStore.TryCreateRunAsync(new NewAgentRun(next,
            [new HarnessEventIntent(new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), HarnessEventKind.RunStateChanged)],
            [], [], new RunCreationIdentity(new string('e', 64), new string('f', 64), 1)), default);
        Assert.Equal(CreateRunOutcome.Created, admitted.Outcome);

        var paths = new DataPaths(Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.jsonl"));
        Directory.CreateDirectory(paths.SessionDirectory);
        var document = new SessionDocument(next.SessionId.Value, "Images", ended, ended,
            visible ? [new SessionMessage("user", caption, "source-user-image", previous.Run.RunId.Value),
                new SessionMessage("ai", "The attachment was received.", "source-ai-image", previous.Run.RunId.Value)] : []);
        await File.WriteAllTextAsync(Path.Combine(paths.SessionDirectory, next.SessionId.Value + ".json"),
            JsonSerializer.Serialize(document));
        var sessions = new SessionStore(paths);
        var bootstrapper = new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore,
            new CryptographicIdGenerator(), TimeProvider.System);

        Assert.Equal(ContextBootstrapOutcome.Captured,
            await bootstrapper.CaptureAsync(next.RunId, next.SessionId, default));
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(next.RunId, default);

        if (visible)
        {
            var user = Assert.Single(snapshot.SessionBase, entry => entry.Role == RuAgent.Harness.Core.Context.ModelContextRole.User
                && entry.SourceId.StartsWith("base:turn:", StringComparison.Ordinal));
            Assert.Contains("1 image attachment", user.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not included", user.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Image, user.Content, StringComparison.Ordinal);
            Assert.Equal(caption, (await sessions.GetAsync(next.SessionId.Value, default))!.Messages[0].Content);
        }
        else
        {
            Assert.Empty(snapshot.SessionBase);
            Assert.Empty(await fixture.PayloadStore.ReadSessionImageCountsAsync(next.SessionId,
                [previous.Run.RunId.Value], default));
        }
        Assert.Empty(await fixture.PayloadStore.ReadSessionImageCountsAsync(
            new SessionId("cccccccccccccccccccccccccccccccc"), [previous.Run.RunId.Value], default));
    }
}
