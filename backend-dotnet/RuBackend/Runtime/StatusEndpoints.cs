using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Runtime;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/status", StatusAsync);
        app.MapGet("/models", ModelsAsync);
        app.MapGet("/models-dev", ModelsDevAsync);
        app.MapPost("/host/shutdown", ShutdownAsync);
    }

    private static async Task<IResult> StatusAsync(
        HttpContext context,
        ISettingsStore settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await settings.ReadAsync(cancellationToken);
            return Results.Json(new
            {
                status = "ok",
                message = "Backend is running and ready for the WinUI3 UI.",
                backend = current.Backend,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Results.Json(new { status = "error", error_code = "status_unavailable", backend = "" });
        }
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
        catch (Exception)
        {
            // Fail closed as an envelope, never an HTML 500: the picker reads
            // error_code, and an unparsable page looks like a dead backend.
            return Results.Json(new { status = "error", error_code = "catalog_unavailable", models = Array.Empty<string>() });
        }
    }

    private static async Task<IResult> ModelsDevAsync(ProviderCatalogService catalog, CancellationToken cancellationToken)
    {
        try
        {
            var providers = await catalog.GetModelsDevAsync(cancellationToken);
            return Results.Json(new
            {
                status = "ok",
                providers = providers.Select(provider => new
                {
                    id = provider.Id,
                    name = provider.Name,
                    base_url = provider.BaseUrl,
                }).ToArray(),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Results.Json(new { status = "error", error_code = "catalog_unavailable", providers = Array.Empty<object>() });
        }
    }

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
