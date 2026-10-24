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
    public async Task Session_export_refuses_a_host_file_name_that_would_escape_the_output_directory()
    {
        var root = Directory.CreateTempSubdirectory("ru-cli-export").FullName;
        var directory = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
        try
        {
            var handler = new StubHandler("""
                {"status":"ok","harness":"codex","display_name":"Codex",
                 "session":{"id":"s1","title":"T","messages":2,
                            "working_directory":"D:\\LLM","created_at":"2026-09-20T09:30:00+00:00"},
                 "files":[{"name":"../escaped.jsonl","content":"x\n"}]}
                """);
            var errors = new StringWriter();
            var commands = new AgentCommands(Session(), new HttpClient(handler), new StringWriter(), errors);

            var exit = await commands.SessionExportAsync(
                new SessionExportCommand("s1", "codex", directory, Json: false),
                CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("session_export_invalid_target", errors.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "escaped.jsonl")));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    [Fact]
    public async Task Session_import_refuses_an_oversized_file_before_reading_or_posting_it()
    {
        var directory = Directory.CreateTempSubdirectory("ru-cli-import").FullName;
        var path = Path.Combine(
            directory,
            "rollout-2026-09-20T09-30-00-11111111-1111-7111-8111-111111111111.jsonl");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                // Sparse: the file is over the cap without writing those bytes.
                stream.SetLength(RuAgent.SessionInterchange.SessionInterchangeLimits.MaxImportContentBytes + 1L);
            }

            var handler = new CountingHandler();
            var errors = new StringWriter();
            var commands = new AgentCommands(Session(), new HttpClient(handler), new StringWriter(), errors);

            var exit = await commands.SessionImportAsync(
                new SessionImportCommand(path, null, null, Json: false),
                CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("import_too_large", errors.ToString(), StringComparison.Ordinal);
            // The rejection happens at the size check: the file is never read into a
            // string and the host is never asked to parse it.
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Session_import_preserves_caller_cancellation_without_printing_an_import_error()
    {
        var directory = Directory.CreateTempSubdirectory("ru-cli-import-cancel").FullName;
        try
        {
            var path = Path.Combine(directory, "transcript.jsonl");
            await File.WriteAllTextAsync(path, "{}\n");
            using var cancellation = new CancellationTokenSource();
            using var http = new HttpClient(new CancellingHandler(cancellation));
            var errors = new StringWriter();
            var commands = new AgentCommands(Session(), http, new StringWriter(), errors);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.SessionImportAsync(
                new SessionImportCommand(path, "claude-code", null, Json: false), cancellation.Token));

            Assert.Equal(string.Empty, errors.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancellingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
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

    /// <summary>Counts requests and answers with an empty ok envelope, so a request that
    /// should never have been made is visible rather than fatal.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
