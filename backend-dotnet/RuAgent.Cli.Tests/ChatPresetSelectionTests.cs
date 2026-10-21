using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatPresetSelectionTests
{
    private static readonly ChatCommand Current = new("C:\\workspace", "sess_123", "standard", false);

    [Theory]
    [InlineData("plan", false)]
    [InlineData("standard", false)]
    [InlineData("full_access", true)]
    public async Task Chosen_intent_uses_existing_bound_policy_update_and_confirmation_header(string preset, bool confirmed)
    {
        var selection = await ChatPresetSelection.SelectAsync(Current, preset, false,
            (_, _) => Task.FromResult<string?>("enable_full_access"), CancellationToken.None);
        var command = Assert.IsType<ChatCommand>(selection.Command);
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var host = PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, "http://127.0.0.1:8000/",
            "synthetic-preset-wire-test", process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('b', 32), DateTimeOffset.UtcNow.ToString("O")));
        using var handler = new PolicyHandler(preset, confirmed);
        using var http = new HttpClient(handler);
        var core = new RunStartCore(new SessionApiClient(host, http), new AgentApiClient(host, http),
            new StringWriter(), new StringWriter());
        var applied = await core.UpdateSessionPresetAsync(command.SessionId!, command.Preset,
            command.ConfirmFullAccess, CancellationToken.None);
        Assert.NotNull(applied);
        Assert.Equal(1, applied.Version);
        Assert.True(handler.Seen);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("standard")]
    public async Task Direct_selection_preserves_context_and_clears_full_access_confirmation(string preset)
    {
        var original = Current with { Preset = "full_access", ConfirmFullAccess = true };
        var result = await ChatPresetSelection.SelectAsync(original, preset, false, null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(original with { Preset = preset, ConfirmFullAccess = false }, result.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep")]
    [InlineData("yes")]
    public async Task Full_access_cancel_or_unrecognized_confirmation_never_changes_the_preset(string? answer)
    {
        var called = false;
        var result = await ChatPresetSelection.SelectAsync(Current, "full_access", false, (list, _) =>
        {
            called = true;
            Assert.Equal("keep", list.Current.Value);
            return Task.FromResult(answer);
        }, CancellationToken.None);
        Assert.True(called);
        Assert.Null(result.Command);
    }

    [Fact]
    public async Task Full_access_is_selected_only_after_the_second_explicit_choice()
    {
        var count = 0;
        var result = await ChatPresetSelection.SelectAsync(Current, null, false, (list, _) =>
        {
            count++;
            if (count == 1)
            {
                Assert.Equal("standard", list.Current.Value);
                Assert.Equal(["plan", "standard", "full_access"], list.Items.Select(item => item.Value));
                return Task.FromResult<string?>("full_access");
            }
            Assert.Equal("keep", list.Current.Value);
            return Task.FromResult<string?>("enable_full_access");
        }, CancellationToken.None);
        Assert.Equal(2, count);
        Assert.Equal(Current with { Preset = "full_access", ConfirmFullAccess = true }, result.Command);
    }

    [Theory]
    [InlineData("full_access", "full_access_confirmation_required")]
    [InlineData("unknown", "invalid_preset")]
    [InlineData(null, "interactive_required")]
    public async Task Invalid_or_unconfirmed_selection_keeps_the_old_command(string? argument, string error)
    {
        var result = await ChatPresetSelection.SelectAsync(Current, argument, false, null, CancellationToken.None);
        Assert.Null(result.Command);
        Assert.StartsWith(error, result.Error);
    }

    [Fact]
    public async Task Retained_run_refuses_the_change_before_opening_a_picker()
    {
        var result = await ChatPresetSelection.SelectAsync(Current, null, true,
            (_, _) => throw new InvalidOperationException("Picker must not open"), CancellationToken.None);
        Assert.Null(result.Command);
        Assert.StartsWith("preset_change_blocked", result.Error);
    }

    [Fact]
    public async Task Cancelled_selection_does_not_commit_full_access()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatPresetSelection.SelectAsync(
            Current, "full_access", false, (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult<string?>("enable_full_access");
            }, cts.Token));
    }

    [Fact]
    public void Preset_command_keeps_pending_images_outside_its_snapshot()
    {
        using var state = new ChatSubmissionState();
        var image = new RuAgent.Cli.Images.CliImageAttachment("image.png", "image/png", "aW1hZ2U=", 1, 1, 5);
        state.Add(image);
        var command = state.CreateSnapshot("/preset plan", "session", "C:\\workspace", "standard", "provider", "model", false);
        Assert.NotNull(command);
        Assert.Empty(command.Images);
        state.Admit(command);
        Assert.Equal(image, Assert.Single(state.Images));
    }

    [Theory]
    [InlineData("/preset plan", "plan", null)]
    [InlineData("/preset standard", "standard", null)]
    [InlineData("/preset full_access", "standard", "full_access_confirmation_required")]
    public async Task Chat_command_updates_status_without_creating_a_model_run(string input, string expected, string? error)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var host = PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, "http://127.0.0.1:8000/",
            "synthetic-preset-test", process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
        using var handler = new ReadHandler();
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        using var reader = new StringReader(input + "\n/status\n/exit\n");
        using var commands = new AgentCommands(host, http, output, errors, reader);
        Assert.Equal(0, await commands.ChatAsync(Current, CancellationToken.None));
        Assert.Contains(expected, TuiText.Strip(output.ToString()));
        Assert.DoesNotContain("/agent/v1/runs", handler.Posts);
        Assert.DoesNotContain("/agent/v1/policies", handler.Posts);
        if (error is not null) Assert.Contains(error, errors.ToString());
        else Assert.DoesNotContain("unknown_command", errors.ToString());
    }

    private sealed class ReadHandler : HttpMessageHandler
    {
        public List<string> Posts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    private sealed class PolicyHandler(string preset, bool confirmed) : HttpMessageHandler
    {
        public bool Seen { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/agent/v1/policies", request.RequestUri!.AbsolutePath);
            Assert.Equal(confirmed, request.Headers.Contains("X-Interactive-Confirmed"));
            if (confirmed) Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-Interactive-Confirmed")));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = payload.RootElement;
            Assert.Equal("session_preset", body.GetProperty("kind").GetString());
            Assert.Equal("sess_123", body.GetProperty("session_id").GetString());
            Assert.Equal(preset, body.GetProperty("preset").GetString());
            Assert.Equal(0, body.GetProperty("expected_version").GetInt64());
            Assert.StartsWith("sps_", body.GetProperty("snapshot_token").GetString());
            Seen = true;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    session_id = "sess_123", preset, version = 1,
                    snapshot_token = body.GetProperty("snapshot_token").GetString(),
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
