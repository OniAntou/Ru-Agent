using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Updates;
using Xunit;
namespace RuAgent.Updates.Tests;
public sealed class DownloadPolicyTests
{
    [Fact]
    public async Task Redirects_never_forward_requests_to_an_untrusted_host()
    {
        var calls = 0;
        using var client = new ReleaseUpdateClient(new Handler(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.test/installer.exe"); return response; }));
        var root = Temp();
        try { Assert.Equal("update_asset_origin_invalid",(await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(Candidate(),root,null,default))).Code); Assert.Equal(1,calls); Assert.Empty(Directory.GetFiles(root)); }
        finally { Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Truncated_or_oversized_downloads_are_never_promoted(int size)
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) }));
        var root = Temp();
        try { Assert.Equal("update_download_size_mismatch",(await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(Candidate(),root,null,default))).Code); Assert.Empty(Directory.GetFiles(root)); }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public async Task Metadata_is_bounded_before_deserialization_and_uses_no_credentials()
    {
        using var client = new ReleaseUpdateClient(new Handler(request => { Assert.Null(request.Headers.Authorization); Assert.Equal("api.github.com",request.RequestUri!.Host); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2*1024*1024+1]) }; }));
        Assert.Equal("update_metadata_too_large",(await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(new("cli","1.0.0","win-x64"),UpdateChannel.Stable,default))).Code);
    }
    [Fact]
    public async Task Cancelled_download_does_not_leave_an_installer()
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4]) }));
        var root = Temp(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Candidate(),root,null,cancellation.Token)); Assert.Empty(Directory.GetFiles(root)); }
        finally { Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData("unknown", "win-x64", 1)]
    [InlineData("cli", "linux-x64", 1)]
    [InlineData("cli", "win-x64", 2)]
    public void Invalid_installation_markers_fail_closed(string product,string architecture,int protocol)
    {
        var root = Temp();
        try { File.WriteAllText(Path.Combine(root,InstalledRelease.MarkerName),JsonSerializer.Serialize(new InstalledRelease(product,"1.0.0",architecture,protocol))); Assert.Equal("update_installation_invalid",Assert.Throws<UpdateException>(() => InstalledRelease.Load(root)).Code); }
        finally { Directory.Delete(root,true); }
    }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(),"ru-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static ReleaseUpdate Candidate() => new("1.1.0","Ru-CLI-1.1.0-win-x64-Setup.exe",new Uri("https://github.com/OniAntou/Ru-Agent/releases/download/v1.1.0/Ru-CLI-1.1.0-win-x64-Setup.exe"),Convert.ToHexString(SHA256.HashData(new byte[4])),4,new Uri("https://github.com/OniAntou/Ru-Agent/releases/tag/v1.1.0"));
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(respond(request)); }
}
