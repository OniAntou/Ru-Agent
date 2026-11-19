using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A follow-up run re-attaches the most recent visible conversation images for a
/// vision-capable pin, bounded by the same four-image / 20 MiB request limits.
/// </summary>
public sealed class SessionImageCarryoverTests
{
    private const string ImageA = "data:image/png;base64,aGVsbG8=";
    private const string ImageB = "data:image/png;base64,d29ybGQ=";
    private const string ImageC = "data:image/png;base64,dGhpcmQ=";

    [Fact]
    public async Task Earlier_visible_images_are_carried_after_the_current_runs_own_images()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, payloads: [ImagePayload("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ImageA)]);
        var current = await SeedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222",
            [ImagePayload("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ImageB)]);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("user", "first image", "source-user-1", first.Run.RunId.Value, 1));
        var carryover = Carryover(fixture, sessions, visionPinned: true);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Equal(new[] { ImageB }, result.CurrentRun);
        Assert.Equal(new[] { ImageA }, result.Earlier);
    }

    [Fact]
    public async Task A_text_only_pin_gets_no_earlier_images()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, payloads: [ImagePayload("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ImageA)]);
        var current = await SeedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222", []);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("user", "first image", "source-user-1", first.Run.RunId.Value, 1));
        var carryover = Carryover(fixture, sessions, visionPinned: false);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Empty(result.CurrentRun);
        Assert.Empty(result.Earlier);
    }

    [Fact]
    public async Task Earlier_images_are_carried_newest_turn_first_up_to_four()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, payloads: [ImagePayload("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ImageA)]);
        var second = await SeedEndedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222",
            [ImagePayload("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ImageB)]);
        var third = await SeedEndedRunAsync(fixture, first.Run, "run_33333333333333333333333333333333",
            [ImagePayload("payload_cccccccccccccccccccccccccccccccc", ImageC)]);
        var current = await SeedRunAsync(fixture, first.Run, "run_44444444444444444444444444444444", []);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("user", "first image", "source-user-1", first.Run.RunId.Value, 1),
            new SessionMessage("ai", "reply", "source-ai-1", first.Run.RunId.Value),
            new SessionMessage("user", "second image", "source-user-2", second.Run.RunId.Value, 1),
            new SessionMessage("user", "third image", "source-user-3", third.Run.RunId.Value, 1));
        var carryover = Carryover(fixture, sessions, visionPinned: true);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Empty(result.CurrentRun);
        Assert.Equal(new[] { ImageC, ImageB, ImageA }, result.Earlier);
    }

    [Fact]
    public async Task Four_is_the_hard_cap_however_many_earlier_images_exist()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, payloads:
        [
            ImagePayload("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ImageA),
            ImagePayload("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ImageB),
        ]);
        var second = await SeedEndedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222",
        [
            ImagePayload("payload_cccccccccccccccccccccccccccccccc", ImageC),
            ImagePayload("payload_dddddddddddddddddddddddddddddddd", "data:image/png;base64,ZHVtbXk="),
        ]);
        var third = await SeedEndedRunAsync(fixture, first.Run, "run_33333333333333333333333333333333",
        [
            ImagePayload("payload_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "data:image/png;base64,bGFzdA=="),
        ]);
        var current = await SeedRunAsync(fixture, first.Run, "run_44444444444444444444444444444444", []);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("user", "first", "source-user-1", first.Run.RunId.Value, 2),
            new SessionMessage("user", "second", "source-user-2", second.Run.RunId.Value, 2),
            new SessionMessage("user", "third", "source-user-3", third.Run.RunId.Value, 1));
        var carryover = Carryover(fixture, sessions, visionPinned: true);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Equal(4, result.Earlier.Count);
        // Newest turn first: the third turn's image, then the second turn's two, then the
        // first image of the first turn.
        Assert.Equal("data:image/png;base64,bGFzdA==", result.Earlier[0]);
        Assert.Equal(ImageC, result.Earlier[1]);
        Assert.Equal("data:image/png;base64,ZHVtbXk=", result.Earlier[2]);
        Assert.Equal(ImageA, result.Earlier[3]);
    }

    [Fact]
    public async Task A_current_runs_own_message_and_non_user_rows_carry_nothing_extra()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, payloads: [ImagePayload("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ImageA)]);
        var current = await SeedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222", []);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("ai", "assistant image fact", "source-ai", first.Run.RunId.Value, 1),
            new SessionMessage("user", "current turn", "source-user-current", current.Run.RunId.Value, 1));
        var carryover = Carryover(fixture, sessions, visionPinned: true);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Empty(result.Earlier);
    }

    [Fact]
    public async Task An_unreadable_earlier_source_does_not_fail_the_run()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await SeedBaseEndedAsync(fixture, []);
        var current = await SeedRunAsync(fixture, first.Run, "run_22222222222222222222222222222222", []);
        var sessions = await SeedSessionAsync(fixture, first.Run.SessionId.Value,
            new SessionMessage("user", "image missing", "source-user-1",
                "run_99999999999999999999999999999999", 1));
        var carryover = Carryover(fixture, sessions, visionPinned: true);

        var result = await carryover.LoadAsync(current.Run.RunId, default);

        Assert.Empty(result.Earlier);
    }

    [Fact]
    public void The_prompt_notice_names_the_carried_count_without_touching_empty_prompts()
    {
        Assert.Equal("hello", SessionImageCarryover.AppendEarlierImageNotice("hello", 0));
        var noted = SessionImageCarryover.AppendEarlierImageNotice("hello", 2);
        Assert.Contains("Earlier conversation images re-attached for reference: 2 images", noted, StringComparison.Ordinal);
        Assert.Contains("not attachments of the current message", noted, StringComparison.Ordinal);
        Assert.EndsWith("hello", noted, StringComparison.Ordinal);
    }

    private static SessionImageCarryover Carryover(
        BackendHarnessTestFixture fixture,
        ISessionStore sessions,
        bool visionPinned) =>
        new(fixture.PayloadStore, fixture.EventStore, sessions, (_, _) => Task.FromResult(visionPinned));

    private static HarnessPayloadIntent ImagePayload(string id, string dataUrl) =>
        new(new PayloadId(id),
            HarnessPayloadKind.UserImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            Encoding.UTF8.GetBytes(dataUrl));

    private static async Task<NewAgentRun> SeedRunAsync(
        BackendHarnessTestFixture fixture,
        AgentRun template,
        string runId,
        IReadOnlyList<HarnessPayloadIntent> payloads)
    {
        var run = new AgentRun(
            new RunId(runId),
            template.SessionId,
            template.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Created),
            0,
            1,
            1,
            0,
            0,
            template.Preset,
            template.PolicySnapshotVersion,
            template.PolicySnapshotSha256,
            template.CreatedAt,
            template.CreatedAt);
        var request = new NewAgentRun(
            run,
            [new HarnessEventIntent(new EventId(EventIdFor(runId)), HarnessEventKind.RunStateChanged,
                payloadId: payloads.FirstOrDefault(payload => payload.Kind == HarnessPayloadKind.UserPrompt)?.PayloadId)],
            payloads,
            [],
            new RunCreationIdentity(Sha256(runId), Sha256("request:" + runId), 1));
        var result = await fixture.EventStore.TryCreateRunAsync(request, default);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return request;
    }


    private static async Task<NewAgentRun> SeedBaseEndedAsync(
        BackendHarnessTestFixture fixture,
        IReadOnlyList<HarnessPayloadIntent> payloads)
    {
        var seeded = await fixture.SeedRunAsync(payloads: payloads);
        await EndRunAsync(fixture, seeded);
        return seeded;
    }

    private static async Task<NewAgentRun> SeedEndedRunAsync(
        BackendHarnessTestFixture fixture,
        AgentRun template,
        string runId,
        IReadOnlyList<HarnessPayloadIntent> payloads)
    {
        var seeded = await SeedRunAsync(fixture, template, runId, payloads);
        await EndRunAsync(fixture, seeded);
        return seeded;
    }

    private static async Task EndRunAsync(BackendHarnessTestFixture fixture, NewAgentRun seeded)
    {
        var endedAt = BackendHarnessTestFixture.Timestamp.AddSeconds(1);
        var stopped = new AgentRun(
            seeded.Run.RunId,
            seeded.Run.SessionId,
            seeded.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            1,
            2,
            1,
            0,
            0,
            seeded.Run.Preset,
            seeded.Run.PolicySnapshotVersion,
            seeded.Run.PolicySnapshotSha256,
            seeded.Run.CreatedAt,
            endedAt,
            terminalAt: endedAt);
        await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                seeded.Run.RunId,
                0,
                stopped,
                [new HarnessEventIntent(new EventId(EventIdFor(seeded.Run.RunId.Value + "-end")),
                    HarnessEventKind.RunStateChanged)],
                [],
                []),
            default);
    }

    private static async Task<ISessionStore> SeedSessionAsync(
        BackendHarnessTestFixture fixture,
        string sessionId,
        params SessionMessage[] messages)
    {
        var paths = new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.jsonl"));
        Directory.CreateDirectory(paths.SessionDirectory);
        var document = new SessionDocument(
            sessionId,
            "Images",
            BackendHarnessTestFixture.Timestamp,
            BackendHarnessTestFixture.Timestamp,
            messages);
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionDirectory, sessionId + ".json"),
            JsonSerializer.Serialize(document));
        return new SessionStore(paths);
    }

    private static string EventIdFor(string runId) =>
        "evt_" + Sha256(runId)[..32];

    private static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
