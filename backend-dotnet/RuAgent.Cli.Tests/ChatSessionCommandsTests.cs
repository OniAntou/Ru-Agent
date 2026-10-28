using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The interactive session card's command flow: pick to switch, Delete behind a confirm,
/// reload after a delete, and the fallbacks when the open session is the one removed.
/// </summary>
public sealed class ChatSessionCommandsTests
{
    [Fact]
    public async Task Sessions_opens_the_card_and_switches_the_chosen_session()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.Sessions.Add(new ChatSessionChoice("s2", "Second", 4, "ws", null));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s2", DeleteRequested: false));
        var commands = fake.Build();

        Assert.True(await commands.TryHandleAsync("/sessions", null, CancellationToken.None));

        Assert.Equal(["s2"], fake.Switched);
        Assert.Empty(fake.DeleteAttempts);
    }

    [Fact]
    public async Task Sessions_keeps_the_plain_list_without_a_card_picker()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        var commands = fake.Build(withSessionPicker: false);

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Contains(fake.Output, line => line.Contains("Sessions (showing up to", StringComparison.Ordinal));
        Assert.Contains(fake.Output, line => line.Contains("First", StringComparison.Ordinal));
        Assert.Empty(fake.Switched);
    }

    [Fact]
    public async Task Delete_asks_for_confirmation_then_reloads_the_card()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.Sessions.Add(new ChatSessionChoice("s2", "Second", 4, "ws", null));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s2", DeleteRequested: true));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome(null, DeleteRequested: false));
        fake.ConfirmAnswers.Enqueue("delete");
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Equal(["s2"], fake.DeleteAttempts);
        Assert.Contains(fake.ConfirmTitles, title => title.Contains("Second", StringComparison.Ordinal));
        Assert.Contains(fake.Output, line => line.Contains("session deleted · Second", StringComparison.Ordinal));
        Assert.Empty(fake.Switched);
    }

    [Fact]
    public async Task Delete_cancelled_by_the_confirm_reopens_the_card()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome(null, DeleteRequested: false));
        fake.ConfirmAnswers.Enqueue("cancel");
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Empty(fake.DeleteAttempts);
        Assert.Equal(2, fake.SessionPickerCalls);
    }

    [Fact]
    public async Task Deleting_the_open_session_moves_to_the_newest_remaining_one()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.Sessions.Add(new ChatSessionChoice("s2", "Second", 4, "ws", null));
        fake.Current = "s1";
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        fake.ConfirmAnswers.Enqueue("delete");
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Equal(["s1"], fake.DeleteAttempts);
        Assert.Equal(["s2"], fake.Switched);
    }

    [Fact]
    public async Task Deleting_the_last_session_starts_a_fresh_one()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.Current = "s1";
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        fake.ConfirmAnswers.Enqueue("delete");
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Equal(1, fake.Created);
        Assert.Equal(["new-1"], fake.Switched);
    }

    [Fact]
    public async Task Delete_failure_reports_and_reopens_the_card()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome(null, DeleteRequested: false));
        fake.ConfirmAnswers.Enqueue("delete");
        fake.DeleteResult = new SessionDeleteResult(Deleted: false, Message: "boom");
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Contains(fake.Errors, line => line.Contains("session_delete_failed: boom", StringComparison.Ordinal));
        Assert.Equal(2, fake.SessionPickerCalls);
    }

    [Fact]
    public async Task Partial_cleanup_reads_as_deleted_with_the_hosts_retry_hint()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome(null, DeleteRequested: false));
        fake.ConfirmAnswers.Enqueue("delete");
        fake.DeleteResult = new SessionDeleteResult(Deleted: true, Message: SessionDeleteResult.PartialArtifactsMessage);
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Contains(
            fake.Output,
            line => line.Contains("session deleted · First", StringComparison.Ordinal)
                && line.Contains(SessionDeleteResult.PartialArtifactsMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resume_without_an_id_lists_sessions_when_the_sequential_engine_has_no_picker()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        var commands = fake.Build(withSessionPicker: false, withPicker: false);

        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);

        Assert.Empty(fake.Switched);
        Assert.Contains(fake.Output, line => line.Contains("First", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resume_with_an_id_switches_without_opening_the_card()
    {
        var fake = new FakeSessions();
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", "id:s9", CancellationToken.None);

        Assert.Equal(["s9"], fake.Switched);
        Assert.Equal(0, fake.SessionPickerCalls);
    }

    [Fact]
    public async Task Busy_blocks_changing_sessions_but_not_listing_them()
    {
        var fake = new FakeSessions();
        fake.Sessions.Add(new ChatSessionChoice("s1", "First", 2, "ws", null));
        fake.Busy = true;
        var commands = fake.Build();

        await commands.TryHandleAsync("/sessions", "id:s9", CancellationToken.None);
        Assert.Contains(fake.Errors, line => line.Contains("session_busy", StringComparison.Ordinal));

        // The card still opens while a run is live; choosing and deleting are refused.
        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: false));
        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);
        Assert.Contains(fake.Errors, line => line.Contains("before changing sessions", StringComparison.Ordinal));
        Assert.Empty(fake.Switched);

        fake.SessionAnswers.Enqueue(new SessionPickerOutcome("s1", DeleteRequested: true));
        await commands.TryHandleAsync("/sessions", null, CancellationToken.None);
        Assert.Contains(fake.Errors, line => line.Contains("before deleting a session", StringComparison.Ordinal));
        Assert.Empty(fake.DeleteAttempts);
        Assert.Equal(2, fake.SessionPickerCalls);
    }

    [Fact]
    public void ToChoiceList_marks_the_current_session_and_keeps_identity_in_the_detail()
    {
        var sessions = new[]
        {
            new ChatSessionChoice("s1", "First", 2, "D:/LLM", null),
            new ChatSessionChoice("s2", "Second", 4, "ws", null),
        };

        var list = ChatSessionWorkflow.ToChoiceList(sessions, "s2");

        Assert.Equal("sessions", list.Title);
        Assert.Equal("First", list.Items[0].Label);
        Assert.Equal("2 msg · D:/LLM", list.Items[0].Detail);
        Assert.Equal("Second (current)", list.Items[1].Label);
        Assert.Equal(1, list.Selected);
        Assert.Contains("del delete", list.Footer, StringComparison.Ordinal);
    }

    private sealed class FakeSessions
    {
        public List<ChatSessionChoice> Sessions { get; } = [];
        public List<string> Switched { get; } = [];
        public List<string> DeleteAttempts { get; } = [];
        public List<string> DeletedSessions { get; } = [];
        public List<string> Output { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> ConfirmTitles { get; } = [];
        public Queue<SessionPickerOutcome> SessionAnswers { get; } = new();
        public Queue<string?> ConfirmAnswers { get; } = new();
        public SessionDeleteResult? DeleteResult { get; set; }
        public string Current { get; set; } = "s1";
        public bool Busy { get; set; }
        public int Created { get; private set; }
        public int SessionPickerCalls { get; private set; }

        public ChatSessionCommands Build(bool withSessionPicker = true, bool withPicker = true)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            return new ChatSessionCommands(
                () => Busy,
                () => Current,
                _ =>
                {
                    Created++;
                    return Task.FromResult("new-" + Created);
                },
                (query, _) => Task.FromResult<IReadOnlyList<ChatSessionChoice>>(Filter(query)),
                id =>
                {
                    Switched.Add(id);
                    return Task.CompletedTask;
                },
                withPicker
                    ? (list, _) =>
                    {
                        ConfirmTitles.Add(list.Title);
                        return Task.FromResult(ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : null);
                    }
                    : null,
                withSessionPicker
                    ? (_, _) =>
                    {
                        SessionPickerCalls++;
                        return Task.FromResult(SessionAnswers.Count > 0
                            ? SessionAnswers.Dequeue()
                            : new SessionPickerOutcome(null, DeleteRequested: false));
                    }
                    : null,
                withSessionPicker
                    ? (id, _) =>
                    {
                        DeleteAttempts.Add(id);
                        var result = DeleteResult ?? new SessionDeleteResult(Deleted: true, Message: null);
                        if (result.Deleted)
                        {
                            DeletedSessions.Add(id);
                        }

                        return Task.FromResult(result);
                    }
                    : null,
                (writer, line) =>
                {
                    (ReferenceEquals(writer, error) ? Errors : Output).Add(line);
                    return Task.CompletedTask;
                },
                output,
                error,
                json: false);
        }

        private IReadOnlyList<ChatSessionChoice> Filter(string? query)
        {
            var remaining = Sessions.Where(session => !DeletedSessions.Contains(session.Id)).ToList();
            if (string.IsNullOrWhiteSpace(query))
            {
                return remaining;
            }

            return remaining
                .Where(session => session.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
