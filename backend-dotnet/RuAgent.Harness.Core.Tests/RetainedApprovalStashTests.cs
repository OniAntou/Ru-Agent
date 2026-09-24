using System.Text.Json;
using RuAgent.Harness.Core.Approval;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class RetainedApprovalStashTests
{
    private static readonly RunId Run = new("run_77777777777777777777777777777777");
    private static readonly ApprovalId Approval = new("apr_77777777777777777777777777777777");
    private static readonly ApprovalId OtherApproval = new("apr_88888888888888888888888888888888");

    [Fact]
    public void Stash_roundtrips_and_take_clears_the_slot()
    {
        var stash = new InMemoryRetainedApprovalStash();
        var step = Retained(
            Approval,
            new ModelStepId("step_77777777777777777777777777777777"));

        stash.Stash(Run, step);

        Assert.True(stash.TryPeek(Run, out var peeked));
        Assert.Same(step, peeked);
        Assert.True(stash.TryTake(Run, Approval, out var taken));
        Assert.Same(step, taken);
        Assert.False(stash.TryPeek(Run, out _));
    }

    [Fact]
    public void Take_with_wrong_approval_keeps_the_slot()
    {
        var stash = new InMemoryRetainedApprovalStash();
        stash.Stash(Run, Retained(Approval, new ModelStepId("step_77777777777777777777777777777777")));

        Assert.False(stash.TryTake(Run, OtherApproval, out _));
        Assert.True(stash.TryPeek(Run, out _));
    }

    [Fact]
    public void Stash_overwrites_and_drop_removes()
    {
        var stash = new InMemoryRetainedApprovalStash();
        stash.Stash(Run, Retained(Approval, new ModelStepId("step_77777777777777777777777777777777")));
        var newer = Retained(OtherApproval, new ModelStepId("step_88888888888888888888888888888888"));
        stash.Stash(Run, newer);

        Assert.True(stash.TryTake(Run, OtherApproval, out var taken));
        Assert.Same(newer, taken);

        stash.Stash(Run, newer);
        stash.Drop(Run);
        Assert.False(stash.TryPeek(Run, out _));
    }

    [Fact]
    public void Stash_sweeps_expired_entries_before_replacing_a_slot()
    {
        var now = new MutableTime(DateTimeOffset.UtcNow);
        var stash = new InMemoryRetainedApprovalStash(now);
        stash.Stash(
            Run,
            new RetainedApprovalStep(
                Array.Empty<ToolProposal>(),
                Approval,
                new ModelStepId("step_77777777777777777777777777777777"),
                now.UtcNow));
        now.Advance(ApprovalState.DefaultLifetime + TimeSpan.FromSeconds(1));
        var replacement = Retained(
            OtherApproval,
            new ModelStepId("step_88888888888888888888888888888888")) with
        {
            StashedAt = now.UtcNow,
        };

        stash.Stash(Run, replacement);

        Assert.True(stash.TryTake(Run, OtherApproval, out var taken));
        Assert.Same(replacement, taken);
    }

    [Fact]
    public void Approval_entries_expire_with_the_durable_approval_lifetime()
    {
        var now = new MutableTime(DateTimeOffset.UtcNow);
        var stash = new InMemoryRetainedApprovalStash(now);
        stash.Stash(
            Run,
            new RetainedApprovalStep(
                Array.Empty<ToolProposal>(),
                Approval,
                new ModelStepId("step_77777777777777777777777777777777"),
                now.UtcNow));

        now.Advance(ApprovalState.DefaultLifetime + TimeSpan.FromSeconds(1));

        Assert.False(stash.TryPeek(Run, out _));
        Assert.False(stash.TryTake(Run, Approval, out _));
    }

    [Fact]
    public void Expired_entries_are_invisible()
    {
        var stash = new InMemoryRetainedApprovalStash();
        var old = new RetainedApprovalStep(
            Array.Empty<ToolProposal>(),
            Approval,
            new ModelStepId("step_77777777777777777777777777777777"),
            DateTimeOffset.UtcNow.AddHours(-25));
        stash.Stash(Run, old);

        Assert.False(stash.TryPeek(Run, out _));
        Assert.False(stash.TryTake(Run, Approval, out _));
    }

    private static RetainedApprovalStep Retained(ApprovalId approvalId, ModelStepId modelStepId) =>
        new(
            new[]
            {
                new ToolProposal(
                    Run,
                    modelStepId,
                    new ToolCallId("call_77777777777777777777777777777777"),
                    "provider_call_1",
                    1,
                    "read",
                    "1",
                    JsonDocument.Parse("{\"op\":\"read\",\"path\":\"README.md\"}").RootElement.Clone()),
            },
            approvalId,
            modelStepId,
            DateTimeOffset.UtcNow);

    private sealed class MutableTime(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
