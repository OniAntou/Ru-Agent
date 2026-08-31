using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessOutboxTests
{
    [Fact]
    public async Task Claim_retry_and_ack_use_a_lease_and_bounded_backoff()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);

        var first = await store.ClaimNextAsync(now, CancellationToken.None);
        var blocked = await store.ClaimNextAsync(now.AddMilliseconds(500), CancellationToken.None);
        var retry = await store.RetryAsync(
            intentId,
            first!.ClaimToken,
            "session_store_transient",
            now.AddSeconds(1),
            CancellationToken.None);
        var tooEarly = await store.ClaimNextAsync(now.AddSeconds(1), CancellationToken.None);
        var second = await store.ClaimNextAsync(retry.NextAttemptAt!.Value, CancellationToken.None);
        var acknowledged = await store.AcknowledgeAsync(
            intentId,
            second!.ClaimToken,
            now.AddSeconds(3),
            CancellationToken.None);
        var repeatedAck = await store.AcknowledgeAsync(
            intentId,
            second.ClaimToken,
            now.AddSeconds(4),
            CancellationToken.None);

        Assert.Equal(1, first.AttemptCount);
        Assert.Null(blocked);
        Assert.Equal(OutboxMutationOutcome.RetryScheduled, retry.Outcome);
        Assert.InRange(retry.NextAttemptAt!.Value - now.AddSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Null(tooEarly);
        Assert.Equal(2, second.AttemptCount);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal(OutboxMutationOutcome.Acknowledged, acknowledged.Outcome);
        Assert.Equal(OutboxMutationOutcome.AlreadyAcknowledged, repeatedAck.Outcome);
    }

    [Fact]
    public async Task Expired_claim_is_reclaimed_with_a_new_token()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await SeedUserOutboxAsync(fixture);
        var store = new HarnessOutboxStore(
            fixture.Options,
            leaseDuration: TimeSpan.FromSeconds(5));
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);

        var first = await store.ClaimNextAsync(now, CancellationToken.None);
        var second = await store.ClaimNextAsync(now.AddSeconds(6), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.ClaimToken, second!.ClaimToken);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task Permanent_failure_is_durable_and_never_claimed_again()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);
        var claim = await store.ClaimNextAsync(now, CancellationToken.None);

        var failed = await store.FailAsync(
            intentId,
            claim!.ClaimToken,
            "session_not_found",
            now.AddSeconds(1),
            CancellationToken.None);
        var next = await store.ClaimNextAsync(now.AddMinutes(10), CancellationToken.None);

        Assert.Equal(OutboxMutationOutcome.Failed, failed.Outcome);
        Assert.Null(next);
    }

    [Fact]
    public async Task Durable_outbox_completion_exposes_the_pending_run_transition()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture, waitingHistory: true);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);
        var claim = await store.ClaimNextAsync(now, CancellationToken.None);
        await store.AcknowledgeAsync(
            intentId,
            claim!.ClaimToken,
            now.AddSeconds(1),
            CancellationToken.None);

        var transitions = await store.ReadPendingRunTransitionsAsync(
            10,
            CancellationToken.None);

        var transition = Assert.Single(transitions);
        Assert.Equal(OutboxRunTransitionKind.AcknowledgeHistory, transition.Kind);
        Assert.Equal(HarnessStoreTestFixture.Run(1), transition.RunId);
        Assert.Equal(HistoryWaitKind.UserBeforeFirstStep, transition.HistoryWaitKind);
        Assert.Null(transition.ErrorCode);
    }

    [Fact]
    public async Task Durable_outbox_failure_exposes_the_pending_run_failure()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture, waitingHistory: true);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);
        var claim = await store.ClaimNextAsync(now, CancellationToken.None);
        await store.FailAsync(
            intentId,
            claim!.ClaimToken,
            "session_not_found",
            now.AddSeconds(1),
            CancellationToken.None);

        var transitions = await store.ReadPendingRunTransitionsAsync(
            10,
            CancellationToken.None);

        var transition = Assert.Single(transitions);
        Assert.Equal(OutboxRunTransitionKind.FailRun, transition.Kind);
        Assert.Equal("session_not_found", transition.ErrorCode);
    }

    [Fact]
    public async Task Empty_outbox_returns_idle()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new HarnessOutboxStore(fixture.Options);
        var work = await store.ReadNextWorkAsync(
            HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.IsType<OutboxWork.Idle>(work);
    }

    [Fact]
    public async Task ReadNextWorkAsync_prioritizes_transition_over_delivery()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture, waitingHistory: true);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);

        var claim = await store.ClaimNextAsync(now, CancellationToken.None);
        await store.AcknowledgeAsync(intentId, claim!.ClaimToken, now.AddSeconds(1), CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes("second message");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(2),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);
        var intent2 = HarnessStoreTestFixture.Event(101);
        var req2 = fixture.NewRun(2, 1, 1, payloads: [payload], outbox: [
            new ConversationOutboxIntent(intent2, HarnessStoreTestFixture.Session(1), payload.PayloadId, ConversationOutboxRole.User, HistoryWaitKind.UserBeforeFirstStep)
        ], status: new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep));
        await fixture.CreateEventStore().TryCreateRunAsync(req2, CancellationToken.None);

        var work = await store.ReadNextWorkAsync(now.AddSeconds(2), CancellationToken.None);
        var transitionWork = Assert.IsType<OutboxWork.Transition>(work);
        Assert.Equal(OutboxRunTransitionKind.AcknowledgeHistory, transitionWork.Value.Kind);
    }

    [Fact]
    public async Task ReadNextWorkAsync_claims_delivery_when_no_transition()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var intentId = await SeedUserOutboxAsync(fixture, waitingHistory: true);
        var store = new HarnessOutboxStore(fixture.Options);
        var now = HarnessStoreTestFixture.Timestamp.AddMinutes(1);

        var work = await store.ReadNextWorkAsync(now, CancellationToken.None);
        var deliveryWork = Assert.IsType<OutboxWork.Delivery>(work);
        Assert.Equal(intentId, deliveryWork.Value.IntentId);
    }

    private static async Task<EventId> SeedUserOutboxAsync(
        HarnessStoreTestFixture fixture,
        bool waitingHistory = true)
    {
        await fixture.RegisterWorkspaceAsync(1);
        var bytes = Encoding.UTF8.GetBytes("visible user message");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(1),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);
        var intentId = HarnessStoreTestFixture.Event(100);
        var request = fixture.NewRun(
            1,
            1,
            1,
            payloads: [payload],
            outbox:
            [
                new ConversationOutboxIntent(
                    intentId,
                    HarnessStoreTestFixture.Session(1),
                    payload.PayloadId,
                    ConversationOutboxRole.User,
                    HistoryWaitKind.UserBeforeFirstStep),
            ],
            status: waitingHistory
                ? new HarnessRunStatus(
                    HarnessRunState.WaitingHistory,
                    HistoryWaitKind.UserBeforeFirstStep)
                : null);
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        return intentId;
    }
}
