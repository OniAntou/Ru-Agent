using System.Net.WebSockets;
using RuBackend.Compatibility;
using RuBackend.Configuration;
using RuBackend.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<BackendOptions>(options =>
{
    builder.Configuration.GetSection(BackendOptions.SectionName).Bind(options);
    if (string.IsNullOrWhiteSpace(options.DesktopToken))
    {
        options.DesktopToken = builder.Configuration["RU_DESKTOP_TOKEN"] ?? string.Empty;
    }
});
builder.Services.AddHttpClient<IUpstreamHttpTransport, HttpUpstreamTransport>();
builder.Services.AddSingleton<IUpstreamWebSocketConnector, ClientWebSocketConnector>();
builder.Services.AddSingleton<HttpForwarder>();
builder.Services.AddSingleton<WebSocketRelay>();
builder.WebHost.UseUrls(
    builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8010");

var app = builder.Build();

app.UseWebSockets();
app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();

app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());
app.MapGet(
    "/chat",
    HandleWebSocketAsync);
app.MapGet(
    "/voice/stream",
    HandleWebSocketAsync);
app.Map(
    "/{**path}",
    (HttpContext context, HttpForwarder forwarder, CancellationToken cancellationToken) =>
        forwarder.ForwardAsync(context, cancellationToken));

app.Run();

static async Task HandleWebSocketAsync(
    HttpContext context,
    IUpstreamWebSocketConnector connector,
    WebSocketRelay relay,
    CancellationToken cancellationToken)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var client = await context.WebSockets.AcceptWebSocketAsync();
    var routeUri = new Uri(
        "http://localhost" + context.Request.Path + context.Request.QueryString,
        UriKind.Absolute);

    WebSocket? upstream = null;
    try
    {
        upstream = await connector.ConnectAsync(routeUri, cancellationToken);
        await relay.RelayAsync(client, upstream, cancellationToken);
    }
    finally
    {
        upstream?.Dispose();
    }
}

public partial class Program { }
