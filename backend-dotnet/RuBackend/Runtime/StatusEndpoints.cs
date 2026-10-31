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
                maintenance_protocol = 1,
                software_version = RuAgent.Updates.InstalledRelease.SoftwareVersion,
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
            // Discovery asks a named custom provider what it serves; nothing is stored (ADR 0034).
            if (context.Request.Query["discover"] == "true")
            {
                var discovered = await catalog.DiscoverCustomModelsAsync(requested, current, cancellationToken);
                return Results.Json(discovered.ToJson());
            }

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
        catch (ProviderCatalogException error)
        {
            return Results.Json(new { status = "error", error_code = error.Code, models = Array.Empty<string>() });
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

    private static async Task<IResult> ShutdownAsync(HttpContext context, IHostApplicationLifetime lifetime,
        RuAgent.Hosting.HostActivityTracker activity, CancellationToken ct)
    {
        if (context.Request.Query["when_idle"] == "true")
        {
            if (!activity.TryBeginMaintenance())
                return Results.Json(new { error_code = "update_busy" }, statusCode: 409);
            bool accepted = false;
            try
            {
                var options = context.RequestServices.GetService<RuAgent.Harness.Persistence.HarnessSqliteOptions>();
                if (options is null) return Results.Json(new { error_code = "update_state_unavailable" }, statusCode: 503);
                var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = options.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false,
                }.ToString();
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_runs WHERE terminal_at_utc IS NULL)";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0)
                    return Results.Json(new { error_code = "update_busy" }, statusCode: 409);
                accepted = true;
                context.Response.Headers["X-Ru-Update-Ready"] = "1";
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.Json(new { error_code = "update_state_unavailable" }, statusCode: 503);
            }
            finally { if (!accepted) activity.CancelMaintenance(); }
        }
        // Auth already enforced by DesktopTokenMiddleware.
        // Stop only after the maintenance acknowledgement has been delivered.
        context.Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
        return Results.Json(new { status = "ok" });
    }
}
