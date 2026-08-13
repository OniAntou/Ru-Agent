using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Configuration;
using RuBackend.Conversation;
using RuBackend.Memory;
using RuBackend.Runtime;
using RuBackend.Security;
using RuBackend.Speech;
using RuBackend.Vts;
using RuBackend.Worker;

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
builder.Services.AddSingleton(BuildDataPaths(builder.Configuration));
builder.Services.AddSingleton(BuildWorkerClientOptions(builder.Configuration));
builder.Services.AddSingleton<LoopbackWorkerClient>();
builder.Services.AddSingleton<IWorkerClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IRuntimeProtocolClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IVoiceProtocolClient>(services =>
    services.GetRequiredService<LoopbackWorkerClient>());
builder.Services.AddSingleton<IRuntimeWorkerClient, LoopbackRuntimeWorkerClient>();
builder.Services.AddSingleton<ConversationTurnGate>();
builder.Services.AddSingleton<IMetadataPolicy, MetadataPolicy>();
builder.Services.AddSingleton<IStreamSanitizer, StreamSanitizer>();
builder.Services.AddSingleton<IConversationCoordinator, ConversationCoordinator>();
builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
builder.Services.AddSingleton<IRuntimeSettingsLoader, LoopbackRuntimeSettingsLoader>();
builder.Services.AddSingleton<ISettingsTransitionService, SettingsTransitionService>();
builder.Services.AddSingleton<ISessionStore, SessionStore>();
builder.Services.AddSingleton<IHistoryStore, HistoryStore>();
builder.Services.AddSingleton<IActivityLogStore, ActivityLogStore>();
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
ChatWebSocketEndpoint.Map(app);
VoiceStreamEndpoint.Map(app);
BusinessEndpoints.Map(app);
MemoryEndpoints.Map(app);
StatusEndpoints.Map(app);
VoiceEndpoints.Map(app);
ObservationEndpoints.Map(app);
SttEndpoints.Map(app);
VtsEndpoints.Map(app);

app.Run();

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
        ReadOnly = false,
    };
}

static DataPaths BuildDataPaths(IConfiguration configuration)
{
    var dataRoot = configuration["RU_DATA_ROOT"]
        ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(userProfile))
    {
        userProfile = Directory.GetCurrentDirectory();
    }

    return DataPaths.Resolve(
        Directory.GetCurrentDirectory(),
        userProfile,
        dataRoot);
}

static WorkerClientOptions BuildWorkerClientOptions(IConfiguration configuration)
{
    var workerUrl = configuration["RuBackend:WorkerUrl"];
    var workerToken = configuration["RU_WORKER_TOKEN"]
        ?? Environment.GetEnvironmentVariable("RU_WORKER_TOKEN")
        ?? string.Empty;

    return new WorkerClientOptions
    {
        Uri = string.IsNullOrWhiteSpace(workerUrl)
            ? new Uri("ws://127.0.0.1:5053/worker/v1")
            : new Uri(workerUrl, UriKind.Absolute),
        Token = workerToken,
    };
}

public partial class Program { }
