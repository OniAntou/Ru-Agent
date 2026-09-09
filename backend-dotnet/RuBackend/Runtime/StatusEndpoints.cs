using RuBackend.Configuration;
using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Runtime;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/status", HandleAsync);
        app.MapGet("/models", ModelsAsync);
        app.MapPost("/host/shutdown", ShutdownAsync);
    }

    private static async Task<IResult> ModelsAsync(HttpContext context, ISettingsStore settings,
        ProviderCatalogService catalog, CancellationToken cancellationToken)
    {
        var current = await settings.ReadAsync(cancellationToken);
        var requested = context.Request.Query["backend"].ToString();
        try
        {
            var result = requested.Length == 0
                ? await catalog.GetAsync(current, context.Request.Query["refresh"] == "true", cancellationToken)
                : await catalog.GetAsync(requested, current, context.Request.Query["refresh"] == "true", cancellationToken);
            return Results.Json(result.ToJson());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnknownProviderAliasException)
        {
            return Results.Json(new { status = "error", error_code = "provider_not_active", models = Array.Empty<string>() });
        }
        catch (Exception error) when (error is ArgumentException or CredentialStoreException)
        {
            return Results.Json(new { status = "error", error_code = "catalog_configuration_error", models = Array.Empty<string>() });
        }
    }

    private static Task HandleAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        Microsoft.Extensions.Options.IOptions<BackendOptions> options,
        CancellationToken cancellationToken) =>
        RuntimeEndpointRelay.SendAsync(
            context,
            workerClient,
            loggerFactory,
            "RuBackend.Status",
            "Runtime worker is not ready.",
            options.Value.MaxRequestBodyBytes,
            cancellationToken);

    private static IResult ShutdownAsync(IHostApplicationLifetime lifetime)
    {
        // Auth already enforced by DesktopTokenMiddleware.
        // HTTP delivery completes before the host stops: Minimal-API IResult
        // execution flushes the response before this handler returns, so stop
        // on the next thread-pool yield instead of a wall-clock delay.
        _ = Task.Factory.StartNew(
            () => lifetime.StopApplication(),
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);
        return Results.Json(new { status = "ok" });
    }
}
