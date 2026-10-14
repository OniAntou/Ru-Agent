using System.Diagnostics;
using System.Net;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using RuAgent.Updates;
using Xunit;
namespace RuAgent.Client.Tests;
public sealed class UpdateMaintenanceTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"maintenance_protocol\":\"1\"}")]
    public async Task Old_hosts_are_never_sent_an_idle_shutdown(string status)
    {
        var posts = 0;
        using var fixture = new Descriptor();
        var handler = new Handler(request => { if (request.Method == HttpMethod.Post) posts++; return new(HttpStatusCode.OK) { Content = new StringContent(status) }; });
        var error = await Assert.ThrowsAsync<UpdateException>(() => UpdateMaintenance.PrepareAsync(default, fixture.Locator,handler));
        Assert.Equal("update_host_protocol_unsupported",error.Code); Assert.Equal(0,posts);
    }
    [Fact]
    public async Task Requires_explicit_acknowledgement_and_sends_only_local_bearer()
    {
        using var fixture = new Descriptor();
        var handler = new Handler(request => {
            Assert.Equal("127.0.0.1",request.RequestUri!.Host); Assert.Equal(new string('t',64),request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"maintenance_protocol\":1}") };
            Assert.Equal("?when_idle=true",request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        Assert.Equal("update_host_prepare_failed",(await Assert.ThrowsAsync<UpdateException>(() => UpdateMaintenance.PrepareAsync(default,fixture.Locator,handler))).Code);
    }
    [Fact]
    public async Task Busy_hosts_preserve_runs_after_bounded_retries()
    {
        using var fixture = new Descriptor(); var posts=0;
        var handler = new Handler(request => {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"maintenance_protocol\":1}") };
            posts++; return new(HttpStatusCode.Conflict);
        });
        Assert.Equal("update_busy",(await Assert.ThrowsAsync<UpdateException>(() => UpdateMaintenance.PrepareAsync(default,fixture.Locator,handler))).Code);
        Assert.Equal(3,posts);
    }
    private sealed class Descriptor : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),"ru-update-client-" + Guid.NewGuid().ToString("N"));
        public PublicHostLocator Locator { get; }
        public Descriptor()
        {
            Directory.CreateDirectory(_root); Locator = new(_root);
            using var process = Process.GetCurrentProcess();
            var descriptor = new RuntimeDescriptor(1,"http://127.0.0.1:8000/",new string('t',64),process.Id,process.StartTime.ToUniversalTime().ToString("O"),new string('a',32),DateTimeOffset.UtcNow.ToString("O"));
            File.WriteAllText(Locator.DescriptorPath,JsonSerializer.Serialize(descriptor,RuntimeDescriptor.JsonOptions));
        }
        public void Dispose() => Directory.Delete(_root,true);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(respond(request)); }
}
