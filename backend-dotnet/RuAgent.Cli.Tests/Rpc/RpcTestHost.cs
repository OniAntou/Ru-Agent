using System.Net;
using System.Text;
using RuAgent.Client;

namespace RuAgent.Cli.Tests.Rpc;

internal static class RpcTestHost
{
    internal const string TestBearer = "test-bearer";

    internal static PublicHostSession CreateSession(string bearer = TestBearer)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    internal static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var fake = new FakeHandler(handler);
        return new HttpClient(fake);
    }

    internal static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var fake = new FakeHandler(handler);
        return new HttpClient(fake);
    }

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
