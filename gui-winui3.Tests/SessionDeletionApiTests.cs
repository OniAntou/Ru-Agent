using System.Net;
using System.Net.Sockets;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionDeletionApiTests
{
    [Fact]
    public async Task DeleteSessionAsync_reports_partial_cleanup_as_deleted_with_the_backend_message()
    {
        // The backend answers HTTP 200 with status:"error" once the session file is
        // already gone and only artifacts remain. The old boolean read that as a
        // failed delete and told the user the chat was still there.
        const string partialMessage = "Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại.";
        using var server = new SessionDeleteServer(
            "{\"status\":\"error\",\"message\":\"" + partialMessage + "\"}");
        using var api = new ApiService("test-secret", server.Port);

        var outcome = await api.DeleteSessionAsync("11111111111111111111111111111111");

        Assert.False(outcome.Deleted);
        Assert.True(outcome.Partial);
        Assert.Equal(partialMessage, outcome.Message);
        await server.RequestHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DeleteSessionAsync_rejects_an_application_error_returned_with_http_200()
    {
        using var server = new SessionDeleteServer(
            "{\"status\":\"error\",\"message\":\"session delete failed\"}");
        using var api = new ApiService("test-secret", server.Port);

        var outcome = await api.DeleteSessionAsync("11111111111111111111111111111111");

        Assert.False(outcome.Deleted);
        Assert.False(outcome.Partial);
        Assert.Equal("session delete failed", outcome.Message);
        await server.RequestHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DeleteSessionAsync_reports_the_ok_envelope_as_deleted()
    {
        using var server = new SessionDeleteServer("{\"status\":\"ok\"}");
        using var api = new ApiService("test-secret", server.Port);

        var outcome = await api.DeleteSessionAsync("11111111111111111111111111111111");

        Assert.True(outcome.Deleted);
        Assert.False(outcome.Partial);
        await server.RequestHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class SessionDeleteServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public SessionDeleteServer(string responseBody)
        {
            ResponseBody = responseBody;
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = HandleOneRequestAsync();
        }

        public int Port { get; }

        public string ResponseBody { get; }

        public TaskCompletionSource<bool> RequestHandled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            _listener.Close();
        }

        private async Task HandleOneRequestAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                Assert.Equal(HttpMethod.Delete.Method, context.Request.HttpMethod);
                byte[] bytes = Encoding.UTF8.GetBytes(ResponseBody);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
                RequestHandled.TrySetResult(true);
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
