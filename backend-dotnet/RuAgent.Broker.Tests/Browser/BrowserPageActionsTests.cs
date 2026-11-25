using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

/// <summary>
/// The page actions of ADR 0055's 2026-11-25 amendment against real browsers: select, find,
/// scroll, hover, dialogs, screenshots, uploads, history moves and popups as session tabs.
/// Each engine family runs the same flow; a family that is not installed is skipped.
/// </summary>
public sealed class BrowserPageActionsTests
{
    private const string MainHtml = """
        <!doctype html>
        <html>
        <head><title>Main</title></head>
        <body>
          <select id="country"><option value="us">United States</option><option value="vn">Viet Nam</option></select>
          <button id="ask" onclick="document.getElementById('answer').textContent = String(confirm('Delete?'))">Ask</button>
          <button id="name" onclick="document.getElementById('answer').textContent = String(prompt('Name?'))">Name</button>
          <div id="answer">none</div>
          <span id="tip" style="display:none">Tip shown</span>
          <button id="hover" onmouseover="document.getElementById('tip').style.display='inline'">Hover me</button>
          <a id="pop" href="/second" target="_blank">Open second</a>
          <input id="file" type="file">
          <div style="height:3000px">tall</div>
          <p id="bottom">Bottom marker</p>
        </body>
        </html>
        """;

    private const string SecondHtml = """
        <!doctype html>
        <html><head><title>Second</title></head><body><h1>Second page</h1></body></html>
        """;

    [BrowserFact]
    public Task Chromium_runs_the_page_actions() => RunAsync(new ChromiumBrowserEngine());

    [FirefoxFact]
    public Task Firefox_runs_the_page_actions() => RunAsync(new FirefoxBrowserEngine());

    private static async Task RunAsync(IBrowserEngine engine)
    {
        await using var server = new PageServer();
        await using var service = new BrowserUseService(engine);
        var uploadFile = Path.Combine(Path.GetTempPath(), "ru-upload-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(uploadFile, "upload");
        try
        {
            Assert.Null((await CallAsync(service, "open", ("url", server.Url))).ErrorCode);
            var snapshot = await CallAsync(service, "snapshot");
            Assert.Null(snapshot.ErrorCode);
            string Ref(string name) => snapshot.Output["elements"]!.AsArray()
                .First(item => item!["name"]!.GetValue<string>().Contains(name, StringComparison.Ordinal))!["ref"]!.GetValue<string>();
            var combobox = snapshot.Output["elements"]!.AsArray().First(item => item!["role"]!.GetValue<string>() == "combobox")!["ref"]!.GetValue<string>();

            var select = await CallAsync(service, "select", ("ref", combobox), ("value", "Viet Nam"));
            Assert.Null(select.ErrorCode);
            Assert.Equal("\"vn\"", await EvalAsync(service, "document.getElementById('country').value"));

            Assert.Null((await CallAsync(service, "hover", ("ref", Ref("Hover me")))).ErrorCode);
            Assert.Equal("\"inline\"", await EvalAsync(service, "document.getElementById('tip').style.display"));

            var find = await CallAsync(service, "find", ("query", "bottom marker"));
            var bottom = Assert.Single(find.Output["matches"]!.AsArray())!["ref"]!.GetValue<string>();
            Assert.Null((await CallAsync(service, "scroll", ("ref", bottom))).ErrorCode);
            Assert.NotEqual("0", await EvalAsync(service, "Math.round(window.scrollY)"));
            Assert.Null((await CallAsync(service, "scroll", ("direction", "up"), ("amount", 20))).ErrorCode);
            Assert.Null((await CallAsync(service, "scroll", ("direction", "up"), ("amount", 20))).ErrorCode);
            Assert.Equal("0", await EvalAsync(service, "Math.round(window.scrollY)"));

            // A confirm is dismissed by default; the answer is reported with the action.
            var ask = await CallAsync(service, "click", ("ref", Ref("Ask")));
            Assert.Null(ask.ErrorCode);
            Assert.Equal("\"false\"", await EvalAsync(service, "document.getElementById('answer').textContent"));
            var dialogs = ask.Output["dialogs"] as JsonArray ?? (await CallAsync(service, "snapshot")).Output["dialogs"] as JsonArray;
            Assert.Equal("confirm", Assert.Single(dialogs!)!["kind"]!.GetValue<string>());

            Assert.Null((await CallAsync(service, "dialog", ("accept", true), ("text", "Ada"))).ErrorCode);
            Assert.Null((await CallAsync(service, "click", ("ref", Ref("Name")))).ErrorCode);
            Assert.Equal("\"Ada\"", await EvalAsync(service, "document.getElementById('answer').textContent"));

            var shot = await service.ExecuteAsync(
                "run", "session", new JsonObject { ["action"] = "screenshot" }, CancellationToken.None,
                new BrowserCallOptions(ImageInputAccepted: true));
            Assert.Null(shot.ErrorCode);
            Assert.True(shot.Image!.Bytes.Length > 100);
            Assert.True(shot.Output["width"]!.GetValue<int>() > 100);

            // The snapshot lists the file input as a textbox; it is the page's only one.
            var refs = (await CallAsync(service, "snapshot")).Output["elements"]!.AsArray();
            var fileRef = refs.Last(item => item!["role"]!.GetValue<string>() == "textbox")!["ref"]!.GetValue<string>();
            var upload = await service.ExecuteAsync(
                "run", "session", new JsonObject { ["action"] = "upload", ["ref"] = fileRef }, CancellationToken.None,
                new BrowserCallOptions(UploadPaths: [uploadFile]));
            Assert.Null(upload.ErrorCode);
            Assert.Equal("1", await EvalAsync(service, "document.getElementById('file').files.length"));

            // A popup the page opens becomes a session tab.
            Assert.Null((await CallAsync(service, "click", ("ref", Ref("Open second")))).ErrorCode);
            JsonArray? tabs = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                tabs = (await CallAsync(service, "tabs")).Output["tabs"] as JsonArray;
                if (tabs is { Count: 2 }) break;
                await Task.Delay(200);
            }

            Assert.Equal(2, tabs!.Count);
            Assert.Null((await CallAsync(service, "switch_tab", ("tab", 2))).ErrorCode);
            await WaitForAsync(async () => await EvalAsync(service, "document.title") == "\"Second\"");
            Assert.Null((await CallAsync(service, "close_tab")).ErrorCode);

            Assert.Null((await CallAsync(service, "navigate", ("url", server.Url + "second"))).ErrorCode);
            var back = await CallAsync(service, "back");
            Assert.Null(back.ErrorCode);
            Assert.Equal("Main", back.Output["title"]!.GetValue<string>());
            var forward = await CallAsync(service, "forward");
            Assert.Null(forward.ErrorCode);
            Assert.Equal("Second", forward.Output["title"]!.GetValue<string>());
            Assert.Null((await CallAsync(service, "reload")).ErrorCode);
        }
        finally
        {
            await CallAsync(service, "close");
            File.Delete(uploadFile);
        }
    }

    private static async Task<string> EvalAsync(BrowserUseService service, string expression)
    {
        var result = await CallAsync(service, "eval", ("expression", expression));
        Assert.Null(result.ErrorCode);
        return result.Output["result"]?.ToJsonString() ?? "null";
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }

        Assert.True(await condition(), "The page did not reach the expected state in time.");
    }

    private static Task<ToolExecutionOutcome> CallAsync(BrowserUseService service, string action, params (string Name, JsonNode? Value)[] arguments)
    {
        var payload = new JsonObject { ["action"] = action };
        foreach (var (name, value) in arguments) payload[name] = value?.DeepClone();
        return service.ExecuteAsync("run", "session", payload, CancellationToken.None).AsTask();
    }

    /// <summary>Serves the main page at / and the second page at /second over loopback HTTP.</summary>
    internal sealed class PageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public PageServer()
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _serving = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // The accept loop ends when the listener is stopped.
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(client));
            }
        }

        private static async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0) return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (request.Length > 16 * 1024) return;
                    }

                    var firstLine = request.ToString().Split("\r\n", 2, StringSplitOptions.None)[0];
                    string? html = firstLine.Contains(" / HTTP/", StringComparison.Ordinal) ? MainHtml
                        : firstLine.Contains(" /second HTTP/", StringComparison.Ordinal) ? SecondHtml
                        : null;
                    var body = Encoding.UTF8.GetBytes(html ?? "not found");
                    var head = Encoding.ASCII.GetBytes(
                        (html is null ? "HTTP/1.1 404 Not Found\r\n" : "HTTP/1.1 200 OK\r\n")
                        + "Content-Type: text/html; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                {
                    // The browser may cancel a request; the server stays up for the next one.
                }
            }
        }
    }
}
