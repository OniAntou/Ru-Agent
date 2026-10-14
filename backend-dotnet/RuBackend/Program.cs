using RuBackend.Business;
using RuBackend.Business.Activity;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Configuration;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Workspace;
using RuBackend.Hosting;
using RuBackend.Providers;
using RuBackend.Runtime;
using RuBackend.Security;
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

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict,
// acquire single instance, generate credentials, publish descriptor. The
// execution authority itself is composed inside DI.
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

// DI composition groups (registration order matches the previous inline order).
builder.Services
    .AddRuFoundation(
        builder.Configuration,
        bootstrap?.PublicBearer)
    .AddRuSettingsAndSessions(builder.Configuration)
    .AddRuHarnessStores()
    .AddRuAgentRuntime(isManagedRuntime)
    .AddRuActivityAndDiagnostics();
// The Public Contract is a loopback-only surface guarded by a bearer token that
// desktop clients read from local files. A misconfigured or edited BindUrl must
// not silently widen it to every interface on the machine. The hosted default
// lives in BackendOptions (standalone) and PublicHostRuntimeOptions (managed),
// so the fallback here cannot drift from the configured surface.
var bindUrl = builder.Configuration["RuBackend:BindUrl"];
if (bindUrl is null)
{
    // Only the managed launcher passes --urls; honoring it there keeps the
    // desktop client's port while a stray ASPNETCORE_URLS cannot retarget a
    // standalone host.
    bindUrl = isManagedRuntime
        ? builder.Configuration["urls"] ?? PublicHostRuntimeOptions.DefaultBindUrl
        : new BackendOptions().BindUrl;
}
if (!IsLoopbackBindUrl(bindUrl))
{
    Console.Error.WriteLine(
        $"RuBackend:BindUrl must bind to a loopback address; refusing to start on '{bindUrl}'.");
    Environment.Exit(4);
}

builder.WebHost.UseUrls(bindUrl);

// Body-bound layering, outermost first: Kestrel -> RequestSizeMiddleware (per route) ->
// the endpoint's own bounded reader. Kestrel's default (30,000,000 bytes) sits below the
// import route's declared bound, so an oversized import was refused by Kestrel with a
// generic 413 before the endpoint could answer import_too_large. This bound stays above
// every application cap; the application caps remain the source of truth.
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = BackendLimits.KestrelMaxRequestBodyBytes);

static bool IsLoopbackBindUrl(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        return false;
    }

    if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
}

var app = builder.Build();

// Never HTML: without this any unhandled exception becomes a 500 text/html
// page, which the GUI and TUI cannot parse. Every failure below this point
// is a JSON envelope instead, and the throw is still logged with its route.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features
        .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(
        error,
        "Unhandled request exception for {Method} {Path}.",
        context.Request.Method,
        context.Request.Path);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new
    {
        status = "error",
        error_code = "internal_error",
        message = "The request could not be completed.",
    });
}));

// Hook shutdown cleanup for managed runtime: authority disposal, descriptor
// cleanup, single-instance release.
if (isManagedRuntime)
{
    var lifetime = app.Lifetime;

    // Resolve the authority at startup so a DI composition failure fails the
    // managed host with a bounded code (exit 4) instead of surfacing as a tool
    // error on the first request. The runtime itself is composed lazily.
    var executionAuthority = ResolveExecutionAuthority(app, bootstrap!);

    lifetime.ApplicationStopping.Register(() =>
    {
        try { executionAuthority.Dispose(); } catch (Exception ex) { app.Logger.LogWarning(ex, "In-process execution authority dispose failed during shutdown."); }
        try { bootstrap!.DescriptorWriter.Cleanup(); } catch (Exception ex) { app.Logger.LogWarning(ex, "Descriptor cleanup failed during shutdown."); }
        try { bootstrap!.SingleInstance.Dispose(); } catch (Exception ex) { app.Logger.LogWarning(ex, "Single-instance release failed during shutdown."); }
    });
}

static InProcessExecutionAuthority ResolveExecutionAuthority(WebApplication app, ManagedRuntimeBootstrap bootstrap)
{
    try
    {
        return app.Services.GetRequiredService<InProcessExecutionAuthority>();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("{\"error_code\":\"in_process_execution_unavailable\"}");
        app.Logger.LogError(ex, "in-process execution authority could not be composed; stopping the host");
        try { bootstrap.DescriptorWriter.Cleanup(); } catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Descriptor cleanup failed during startup failure."); }
        try { bootstrap.SingleInstance.Dispose(); } catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Single-instance release failed during startup failure."); }
        Environment.Exit(4);
        // Environment.Exit never returns; this guard exists only because the
        // compiler requires a terminator here (Exit is not annotated as
        // non-returning), so it is never executed.
        throw new UnreachableException("Environment.Exit(4) did not terminate the process.");
    }
}

app.UseWebSockets();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<DesktopTokenMiddleware>();
app.UseMiddleware<RequestSizeMiddleware>();
app.UseMiddleware<DiagnosticRequestMiddleware>();
// Request activity for the managed host's idle shutdown: an in-flight request (including
// a streaming response) keeps the host alive, and every request resets the idle clock.
app.Use(async (context, next) =>
{
    var activity = context.RequestServices.GetRequiredService<RuAgent.Hosting.HostActivityTracker>();
    if (!activity.TryEnter())
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { status = "error", error_code = "update_in_progress" });
        return;
    }
    try
    {
        await next().ConfigureAwait(false);
    }
    finally
    {
        activity.Exit();
    }
});
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
SessionInterchangeEndpoints.Map(app);
AgentV1Endpoints.Map(app);
DiagnosticEndpoints.Map(app);
StatusEndpoints.Map(app);

using var telemetryProvider = BackendTelemetry.CreateProvider();

app.Run();

public partial class Program { }
