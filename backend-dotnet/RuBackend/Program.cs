using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Agent;
using RuBackend.Configuration;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Worker;
using RuBackend.Harness.Workspace;
using RuBackend.Hosting;
using RuBackend.Providers;
using RuBackend.Runtime;
using RuBackend.Security;
using RuBackend.Worker;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Hosting;
using RuAgent.Broker.Configuration;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var isManagedRuntime = args.Contains("--managed-runtime", StringComparer.Ordinal);
var isDeterministicHarness = !isManagedRuntime
    && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RU_HARNESS_WORKER_TOKEN"));

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict,
// acquire single instance, generate credentials, start Worker/Broker, publish
// descriptor. The manager uses the same log/session root and returns the final
// values after readiness succeeds.
ManagedRuntimeBootstrap? bootstrap = null;
if (isManagedRuntime)
{
    try
    {
        bootstrap = await ManagedRuntimeBootstrapper.StartAsync(builder.Configuration, Directory.GetCurrentDirectory()).ConfigureAwait(false);
    }
    catch (HostStartupException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(ex.ExitCode);
    }
}

var agentWorkerPort = bootstrap?.AgentWorkerPort ?? PublicHostRuntimeOptions.DefaultWorkerPort;
var agentWorkerToken = bootstrap?.AgentWorkerToken ?? string.Empty;

// DI composition groups (registration order matches the previous inline order).
builder.Services
    .AddRuFoundation(
        builder.Configuration,
        bootstrap?.PublicBearer,
        agentWorkerPort,
        agentWorkerToken)
    .AddRuSettingsAndSessions(builder.Configuration)
    .AddRuHarnessStores()
    .AddRuAgentRuntime(isDeterministicHarness, isManagedRuntime)
    .AddRuActivityAndDiagnostics();
builder.WebHost.UseUrls(
    builder.Configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8000");

var app = builder.Build();

// Hook shutdown cleanup for managed runtime: descriptor cleanup, job shutdown
if (isManagedRuntime)
{
    var lifetime = app.Lifetime;
    bootstrap!.WorkerBrokerManager.Exited += (_, _) => lifetime.StopApplication();
    lifetime.ApplicationStopping.Register(() =>
    {
        try { bootstrap.DescriptorWriter.Cleanup(); } catch { }
        try { bootstrap.WorkerBrokerManager.Dispose(); } catch { }
        try { bootstrap.SingleInstance.Dispose(); } catch { }
    });
}

app.UseWebSockets();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();
app.UseMiddleware<DiagnosticRequestMiddleware>();
// Harness store health: disable only /agent/v1/* with agent_store_unavailable
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/agent/v1", StringComparison.OrdinalIgnoreCase))
    {
        var healthLazy = context.RequestServices.GetService<Lazy<Task<HarnessStoreHealth>>>();
        if (healthLazy is not null)
        {
            var health = await healthLazy.Value.ConfigureAwait(false);
            if (!health.IsAvailable)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error_code = "agent_store_unavailable" });
                return;
            }
        }
    }

    await next();
});

app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());

BusinessEndpoints.Map(app);
AgentV1Endpoints.Map(app);
DiagnosticEndpoints.Map(app);
StatusEndpoints.Map(app);

using var telemetryProvider = BackendTelemetry.CreateProvider();

app.Run();

public partial class Program { }
