using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Runtime;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/status", HandleAsync);
        app.MapGet("/models", ModelsAsync);
    }

    private static async Task<IResult> ModelsAsync(HttpContext context, ISettingsStore settings,
        ProviderCatalogService catalog, CancellationToken cancellationToken)
    {
        var current = await settings.ReadAsync(cancellationToken);
        try
        {
            var selection = ProviderSelection.From(current);
            var requested = context.Request.Query["backend"].ToString();
            if (requested.Length != 0 && requested != selection.Alias && requested != selection.Backend)
                return Results.Json(new { status = "error", error_code = "provider_not_active", models = Array.Empty<string>() });
            var result = await catalog.GetAsync(current, context.Request.Query["refresh"] == "true", cancellationToken);
            return Results.Json(result.ToJson());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is ArgumentException or CredentialStoreException)
        {
            return Results.Json(new { status = "error", error_code = "catalog_configuration_error", models = Array.Empty<string>() });
        }
    }

    private static Task HandleAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        RuntimeEndpointRelay.SendAsync(
            context,
            workerClient,
            loggerFactory,
            "RuBackend.Status",
            "Runtime worker is not ready.",
            cancellationToken);
}
