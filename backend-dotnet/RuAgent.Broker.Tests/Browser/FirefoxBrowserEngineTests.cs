using System.Net;
using System.Net.Sockets;
using System.Text;
using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

/// <summary>
/// Real Firefox checks for the WebDriver BiDi engine (ADR 0055); skipped when no Firefox
/// installation is available. Firefox is launched headless with an ephemeral profile, so the
/// tests only need a loopback page server and no network access.
/// </summary>
public sealed class FirefoxBrowserEngineTests
{
    private const string PageHtml = """
        <!doctype html>
        <html>
        <head><title>Ru Firefox Test</title></head>
        <body>
          <h1>Browser test</h1>
          <a href="#next">Next section</a>
          <button id="inc" onclick="document.getElementById('count').textContent = String(Number(document.getElementById('count').textContent) + 1)">Increment</button>
          <div id="count">0</div>
          <input id="name" placeholder="Your name">
          <script>console.log('ready');</script>
        </body>
        </html>
        """;

    [FirefoxFact]
    public async Task Engine_navigates_snapshots_and_drives_a_local_page()
    {
        await using var server = new TestPageServer(PageHtml);
        await using var engine = new FirefoxBrowserEngine();
        var page = await engine.OpenPageAsync(headless: true, CancellationToken.None);
        try
        {
            await engine.NavigateAsync(page, server.Url, CancellationToken.None);

            var snapshot = await engine.SnapshotAsync(page, 150, 20_000, CancellationToken.None);
            Assert.Equal("Ru Firefox Test", snapshot.Title);
            Assert.Equal(server.Url, snapshot.Url);
            Assert.Contains("Browser test", snapshot.Text);
            var link = Assert.Single(snapshot.Elements, element => element.Role == "link");
            Assert.Equal("Next section", link.Name);
            var button = Assert.Single(snapshot.Elements, element => element.Name == "Increment");
            Assert.Equal("button", button.Role);
            var textbox = Assert.Single(snapshot.Elements, element => element.Role == "textbox");
            Assert.Equal("Your name", textbox.Name);

            Assert.Equal("Ru Firefox Test", await engine.TitleAsync(page, CancellationToken.None));

            await engine.ClickAsync(page, button.ElementRef, CancellationToken.None);
            Assert.Equal("\"1\"", await engine.EvaluateAsync(page, "document.getElementById('count').textContent", CancellationToken.None));
            Assert.Equal("null", await engine.EvaluateAsync(page, "void 0", CancellationToken.None));
            Assert.Equal("{\"a\":1}", await engine.EvaluateAsync(page, "({a:1})", CancellationToken.None));

            await engine.TypeAsync(page, textbox.ElementRef, "Ada", replace: false, CancellationToken.None);
            Assert.Equal("\"Ada\"", await engine.EvaluateAsync(page, "document.getElementById('name').value", CancellationToken.None));
            await engine.PressAsync(page, "Backspace", CancellationToken.None);
            Assert.Equal("\"Ad\"", await engine.EvaluateAsync(page, "document.getElementById('name').value", CancellationToken.None));
            await engine.TypeAsync(page, null, "a", replace: false, CancellationToken.None);
            Assert.Equal("\"Ada\"", await engine.EvaluateAsync(page, "document.getElementById('name').value", CancellationToken.None));

            var keyFailure = await Assert.ThrowsAsync<BrowserEngineException>(
                () => engine.PressAsync(page, "Turbo", CancellationToken.None));
            Assert.Equal("browser_key_invalid", keyFailure.Code);

            await engine.FillAsync(page, textbox.ElementRef, "Grace", CancellationToken.None);
            Assert.Equal("\"Grace\"", await engine.EvaluateAsync(page, "document.getElementById('name').value", CancellationToken.None));

            var console = await engine.ConsoleAsync(page, 50, CancellationToken.None);
            Assert.Contains(console, entry => entry.Text.Contains("ready", StringComparison.Ordinal));
            var network = await engine.NetworkAsync(page, 50, CancellationToken.None);
            Assert.Contains(network, entry => entry.Url.StartsWith(server.Url, StringComparison.Ordinal));

            var failure = await Assert.ThrowsAsync<BrowserEngineException>(
                () => engine.ClickAsync(page, "e999", CancellationToken.None));
            Assert.Equal("browser_element_not_found", failure.Code);

            await engine.ClosePageAsync(page, CancellationToken.None);
            var closed = await Assert.ThrowsAsync<BrowserEngineException>(
                () => engine.TitleAsync(page, CancellationToken.None));
            Assert.Equal("browser_not_open", closed.Code);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    [FirefoxFact]
    public async Task Engine_opens_independent_pages_and_dispose_cleans_up()
    {
        await using var server = new TestPageServer(PageHtml);
        var engine = new FirefoxBrowserEngine();
        try
        {
            var first = await engine.OpenPageAsync(headless: true, CancellationToken.None);
            await engine.NavigateAsync(first, server.Url, CancellationToken.None);
            var second = await engine.OpenPageAsync(headless: true, CancellationToken.None);
            await engine.NavigateAsync(second, server.Url, CancellationToken.None);
            Assert.NotEqual(first.TargetId, second.TargetId);
            Assert.Equal(first.SessionId, first.TargetId);

            await engine.ClosePageAsync(first, CancellationToken.None);
            var closed = await Assert.ThrowsAsync<BrowserEngineException>(
                () => engine.TitleAsync(first, CancellationToken.None));
            Assert.Equal("browser_not_open", closed.Code);
            Assert.Equal("Ru Firefox Test", await engine.TitleAsync(second, CancellationToken.None));

            await engine.ClosePageAsync(second, CancellationToken.None);
        }
        finally
        {
            await engine.DisposeAsync();
        }

        var afterDispose = await Assert.ThrowsAsync<BrowserEngineException>(
            () => engine.OpenPageAsync(headless: true, CancellationToken.None));
        Assert.Equal("browser_closed", afterDispose.Code);
    }

    /// <summary>A minimal loopback HTTP server so the test needs no URL reservation or network.</summary>
    private sealed class TestPageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public TestPageServer(string html)
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _serving = Task.Run(() => ServeAsync(html));
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serving.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The accept loop ends when the listener is stopped.
            }

            _stop.Dispose();
        }

        private async Task ServeAsync(string html)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(client, html), _stop.Token);
            }
        }

        private static async Task RespondAsync(TcpClient client, string html)
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
                        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0) return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (request.Length > 16 * 1024) return;
                    }

                    var firstLine = request.ToString().Split("\r\n", 2, StringSplitOptions.None)[0];
                    var isPage = firstLine.Contains(" / HTTP/", StringComparison.Ordinal);
                    var body = isPage
                        ? Encoding.UTF8.GetBytes(html)
                        : Encoding.UTF8.GetBytes("not found");
                    var head = Encoding.ASCII.GetBytes(
                        (isPage ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 404 Not Found\r\n")
                        + "Content-Type: text/html; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(head).ConfigureAwait(false);
                    await stream.WriteAsync(body).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                {
                    // The browser may cancel a request; the server stays up for the next one.
                }
            }
        }
    }
}

/// <summary>A fact that skips when no Firefox installation is available.</summary>
public sealed class FirefoxFactAttribute : FactAttribute
{
    public FirefoxFactAttribute()
    {
        if (FirefoxBrowserEngine.FindInstalledExecutable() is null)
        {
            Skip = "No Mozilla Firefox installation is available.";
        }
    }
}
