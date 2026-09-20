using System.Net;
using System.Text;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The file-writing end of <c>session export</c>, driven against a stub host and a real
/// temporary directory. The layout tests prove where files *would* go; this proves the
/// command actually puts them there, with the bytes the host sent.
/// </summary>
public sealed class SessionInterchangeCommandsTests
{
    [Fact]
    public async Task Session_export_writes_every_file_into_the_directory_it_was_asked_for()
    {
        var directory = Directory.CreateTempSubdirectory("ru-cli-export").FullName;
        try
        {
            var handler = new StubHandler("""
                {"status":"ok","harness":"codex","display_name":"Codex",
                 "session":{"id":"s1","title":"T","messages":2,
                            "working_directory":"D:\\LLM","created_at":"2026-09-20T09:30:00+00:00"},
                 "files":[{"name":"rollout-x.jsonl","content":"a\n"},
                          {"name":"session_index.jsonl","content":"b\n"}]}
                """);
            var output = new StringWriter();
            var commands = new AgentCommands(Session(), new HttpClient(handler), output, new StringWriter());

            var exit = await commands.SessionExportAsync(
                new SessionExportCommand("s1", "codex", directory, Json: false),
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(directory, "rollout-x.jsonl")));
            Assert.Equal("b\n", await File.ReadAllTextAsync(Path.Combine(directory, "session_index.jsonl")));

            // Where it wrote is the one thing a person cannot guess, so it is printed.
            Assert.Contains(Path.Combine(directory, "rollout-x.jsonl"), output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Session_export_reports_a_refusal_from_the_host_instead_of_writing_nothing_silently()
    {
        var directory = Directory.CreateTempSubdirectory("ru-cli-export").FullName;
        try
        {
            var handler = new StubHandler(
                """{"status":"error","message":"Antigravity sessions cannot be written: protobuf payloads."}""");
            var errors = new StringWriter();
            var commands = new AgentCommands(Session(), new HttpClient(handler), new StringWriter(), errors);

            var exit = await commands.SessionExportAsync(
                new SessionExportCommand("s1", "antigravity", directory, Json: false),
                CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("protobuf", errors.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RuAgent.Client.PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            new string('e', 64),
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return RuAgent.Client.PublicHostSession.FromDescriptor(descriptor);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
