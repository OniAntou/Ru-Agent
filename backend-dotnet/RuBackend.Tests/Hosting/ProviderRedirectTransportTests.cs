using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RuBackend.Hosting;
using Xunit;

namespace RuBackend.Tests.Hosting;

public sealed class ProviderRedirectTransportTests
{
    [Fact]
    public async Task Host_model_http_client_returns_cross_origin_redirect_without_following_it()
    {
        var source = new TcpListener(IPAddress.Loopback, 0);
        var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        destination.Start();
        var sourceAddress = (IPEndPoint)source.LocalEndpoint;
        var destinationAddress = (IPEndPoint)destination.LocalEndpoint;
        var redirectTarget = new Uri($"http://127.0.0.1:{destinationAddress.Port}/capture");

        using var serverLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var destinationLifetime = new CancellationTokenSource();
        var sourceRequest = AcceptAndRedirectAsync(source, redirectTarget, serverLifetime.Token);
        var destinationRequest = destination.AcceptTcpClientAsync(destinationLifetime.Token).AsTask();

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            PublicHostServiceRegistration.AddRuAgentRuntime(services, isManagedRuntime: true);
            using var provider = services.BuildServiceProvider();
            var http = provider.GetRequiredService<HttpClient>();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{sourceAddress.Port}/model"))
            {
                Content = new StringContent("synthetic prompt", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic credential");
            request.Headers.Add("x-api-key", "synthetic-key");

            Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, serverLifetime.Token);
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            await sourceRequest;

            await AssertNoRedirectFollowAsync(destinationRequest, destinationLifetime);
        }
        finally
        {
            serverLifetime.Cancel();
            destinationLifetime.Cancel();
            try { await sourceRequest; }
            catch (OperationCanceledException) when (serverLifetime.IsCancellationRequested) { }
            try
            {
                using var redirectedConnection = await destinationRequest;
            }
            catch (OperationCanceledException) when (destinationLifetime.IsCancellationRequested) { }
            source.Stop();
            destination.Stop();
        }
    }

    private static async Task AcceptAndRedirectAsync(TcpListener listener, Uri target, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        var requestHeaders = await ReadHeadersAsync(stream, cancellationToken);
        Assert.Contains("POST /model", requestHeaders, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer synthetic credential", requestHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-api-key: synthetic-key", requestHeaders, StringComparison.OrdinalIgnoreCase);
        await ReadRequestBodyAsync(stream, requestHeaders, cancellationToken);

        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (bytes.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(one.AsMemory(), cancellationToken);
            if (read == 0) break;
            bytes.WriteByte(one[0]);
            var length = bytes.Length;
            if (length >= 4)
            {
                var tail = bytes.GetBuffer();
                if (tail[length - 4] == '\r' && tail[length - 3] == '\n'
                    && tail[length - 2] == '\r' && tail[length - 1] == '\n')
                    break;
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task ReadRequestBodyAsync(Stream stream, string requestHeaders, CancellationToken cancellationToken)
    {
        var contentLengthLine = requestHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLengthLine is null) return;

        var contentLength = int.Parse(contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim());
        var body = new byte[contentLength];
        var readTotal = 0;
        while (readTotal < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(readTotal), cancellationToken);
            if (read == 0) throw new IOException("The provider request ended before its body was complete.");
            readTotal += read;
        }

        Assert.Equal("synthetic prompt", Encoding.UTF8.GetString(body));
    }

    private static async Task AssertNoRedirectFollowAsync(
        Task<TcpClient> destinationRequest,
        CancellationTokenSource destinationLifetime)
    {
        using var observationWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            using var connection = await destinationRequest.WaitAsync(observationWindow.Token);
            Assert.Fail("The Host model client followed a provider redirect to a second origin.");
        }
        catch (OperationCanceledException) when (observationWindow.IsCancellationRequested)
        {
            // No connection reached the redirect target within the bounded observation window.
        }
        finally
        {
            destinationLifetime.Cancel();
            try { await destinationRequest; }
            catch (OperationCanceledException) when (destinationLifetime.IsCancellationRequested) { }
        }
    }
}
