using System.Net.WebSockets;
using RuBackend.Compatibility;
using RuBackend.Configuration;
using RuBackend.Memory;
using RuBackend.Runtime;
using RuBackend.Security;
using RuBackend.Speech;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
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
builder.Services.AddTransient<IRuntimeWorkerClient, FastApiRuntimeWorkerClient>();
builder.Services.AddSingleton(BuildMemorySqliteOptions(builder.Configuration));
builder.Services.AddSingleton<IMemoryRepository, SqliteMemoryRepository>();
builder.Services.AddSingleton<IMemoryPolicy, MemoryPolicy>();
builder.Services.AddSingleton<IMemoryService, MemoryService>();
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
MemoryEndpoints.Map(app);
SttEndpoints.Map(app);
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

static MemorySqliteOptions BuildMemorySqliteOptions(IConfiguration configuration)
{
    var dataRoot = configuration["RU_DATA_ROOT"]
        ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
    var root = string.IsNullOrWhiteSpace(dataRoot)
        ? Path.Combine(Directory.GetCurrentDirectory(), "data", "conversations")
        : dataRoot.Trim();

    return new MemorySqliteOptions
    {
        DatabasePath = configuration["RuBackend:Memory:DatabasePath"]
            ?? Path.Combine(root, "memory.db"),
        LegacyTextsPath = configuration["RuBackend:Memory:LegacyTextsPath"]
            ?? Path.Combine(root, "memory_texts.json"),
        ReadOnly = string.Equals(
            configuration["RuBackend:Ownership:Memory"],
            "shadow",
            StringComparison.OrdinalIgnoreCase),
    };
}

public partial class Program { }
