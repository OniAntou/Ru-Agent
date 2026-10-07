using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class InputCommandTests
{
    private static PublicHostSession CreateSession(string bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private static HttpClient CreateFakeClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new FakeHandler(handler));

    [Fact]
    public async Task Answer_PostsTheAnswerAndPrintsTheResumedRunState()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var inputId = "inp_" + new string('b', 32);
        string? observedUrl = null;
        string? observedAnswer = null;
        var handler = CreateFakeClient(async req =>
        {
            observedUrl = req.RequestUri!.ToString();
            var body = await req.Content!.ReadAsStringAsync();
            observedAnswer = JsonDocument.Parse(body).RootElement.GetProperty("answer").GetString();
            var respJson = JsonSerializer.Serialize(new { input_id = inputId, status = "answered", run_state = "running" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, new StringWriter());
        var cmd = new AnswerCommand(runId, inputId, "staging", Json: false);
        var exit = await commands.AnswerAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotNull(observedUrl);
        Assert.EndsWith($"/agent/v1/runs/{runId}/inputs/{inputId}", observedUrl);
        Assert.Equal("staging", observedAnswer);
        var output = outWriter.ToString();
        Assert.Contains("answered", output);
        Assert.Contains("run_state: running", output);
    }

    [Fact]
    public async Task Answer_Json_PrintsTheVersionedEnvelope()
    {
        var session = CreateSession();
        var runId = "run_" + new string('c', 32);
        var inputId = "inp_" + new string('d', 32);
        var handler = CreateFakeClient(_ =>
        {
            var respJson = JsonSerializer.Serialize(new { input_id = inputId, status = "answered", run_state = "running" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") });
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, new StringWriter());
        var cmd = new AnswerCommand(runId, inputId, "staging", Json: true);
        var exit = await commands.AnswerAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(outWriter.ToString().Trim());
        Assert.Equal("input_answer", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(inputId, document.RootElement.GetProperty("input_id").GetString());
        Assert.Equal("running", document.RootElement.GetProperty("run_state").GetString());
    }

    [Fact]
    public async Task Answer_StaleInput_FailsWithExitOne()
    {
        var session = CreateSession();
        var runId = "run_" + new string('e', 32);
        var inputId = "inp_" + new string('f', 32);
        var handler = CreateFakeClient(_ =>
        {
            var errJson = JsonSerializer.Serialize(new { error_code = "input_stale", message = "already answered" });
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)409) { Content = new StringContent(errJson, Encoding.UTF8, "application/json") });
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, new StringWriter(), errWriter);
        var exit = await commands.AnswerAsync(new AnswerCommand(runId, inputId, "staging", Json: false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("input_stale", errWriter.ToString());
    }

    [Fact]
    public void Parser_Answer_AcceptsAnOptionalAnswer()
    {
        var runId = "run_" + new string('a', 32);
        var inputId = "inp_" + new string('b', 32);

        var withAnswer = AgentCliParser.Parse(new[] { "agent", "answer", runId, inputId, "staging" });
        Assert.True(withAnswer.Success);
        var cmd = Assert.IsType<AnswerCommand>(withAnswer.Command);
        Assert.Equal("staging", cmd.Answer);
        Assert.False(cmd.Json);

        var withoutAnswer = AgentCliParser.Parse(new[] { "agent", "answer", runId, inputId });
        Assert.True(withoutAnswer.Success);
        Assert.Null(Assert.IsType<AnswerCommand>(withoutAnswer.Command).Answer);
    }

    [Fact]
    public void Parser_Answer_RequiresTheAnswerArgumentForJson()
    {
        var runId = "run_" + new string('a', 32);
        var inputId = "inp_" + new string('b', 32);

        var res = AgentCliParser.Parse(new[] { "agent", "answer", runId, inputId, "--json" });

        Assert.False(res.Success);
        Assert.Equal("interactive_required", res.ErrorCode);
    }

    [Theory]
    [InlineData("inp_short")]
    [InlineData("apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("inp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Parser_Answer_RejectsAnInputIdThatIsNotTheMintedFormat(string inputId)
    {
        var runId = "run_" + new string('a', 32);

        var res = AgentCliParser.Parse(new[] { "agent", "answer", runId, inputId, "staging" });

        Assert.False(res.Success);
        Assert.Equal("invalid_input_id", res.ErrorCode);
    }

    [Fact]
    public void Parser_Answer_RejectsAnEmptyAnswer()
    {
        var runId = "run_" + new string('a', 32);
        var inputId = "inp_" + new string('b', 32);

        var res = AgentCliParser.Parse(new[] { "agent", "answer", runId, inputId, string.Empty });

        Assert.False(res.Success);
        Assert.Equal("invalid_answer", res.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
