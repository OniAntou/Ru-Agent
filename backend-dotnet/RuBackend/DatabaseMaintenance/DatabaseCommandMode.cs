using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuBackend.Business;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Maintenance;

namespace RuBackend.DatabaseMaintenance;

internal static class DatabaseCommandMode
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "--database";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!IsRequested(args)) return 2;
        var parsed = Parse(args[1..]);
        if (parsed.Error is not null)
        {
            WriteError(parsed.Error);
            return 2;
        }

        try
        {
            var profile = ResolveProfile(parsed.DataRoot);
            var databasePath = profile.AgentHarnessDatabase;
            var service = new DatabaseMaintenanceService(
                new HarnessSqliteOptions(databasePath),
                Path.Combine(profile.BusinessDataRoot, "harness_key.dat"));
            if (parsed.Operation is "restore" or "compact" or "prune")
            {
                using var lease = DatabaseOwnerLease.Acquire(databasePath);
                using var operation = DatabaseOperationLease.Acquire(databasePath, exclusive: true);
                await DatabaseMaintenanceService.RecoverRestoreIfNeededAsync(databasePath, cancellationToken)
                    .ConfigureAwait(false);
                var mutationResult = await RunOperationAsync(service, profile, parsed, cancellationToken).ConfigureAwait(false);
                WriteResult(mutationResult);
                return 0;
            }

            using var reader = parsed.Operation is "status" or "backup"
                ? DatabaseOperationLease.Acquire(databasePath, exclusive: false) : null;
            if (reader is not null && DatabaseMaintenanceService.IsRestorePending(databasePath))
                throw new DatabaseMaintenanceException("database_restore_recovery_pending");

            var result = await RunOperationAsync(service, profile, parsed, cancellationToken).ConfigureAwait(false);
            WriteResult(result);
            return 0;
        }
        catch (DatabaseMaintenanceException exception)
        {
            WriteError(exception.ErrorCode);
            return exception.ErrorCode == "database_profile_in_use" ? 3 : 1;
        }
        catch (HarnessStoreException exception)
        {
            WriteError(exception.Code);
            return 1;
        }
        catch (SqliteException exception)
        {
            WriteError(exception.SqliteErrorCode is 5 or 6 ? "database_busy" : "database_operation_failed");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteError("cancelled");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            WriteError("database_path_invalid");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteError("database_io_failed");
            return 1;
        }
    }

    private static async Task<object> RunOperationAsync(DatabaseMaintenanceService service, DataPaths profile, ParsedDatabaseCommand parsed, CancellationToken cancellationToken)
    {
        return parsed.Operation switch
        {
            "status" => await service.GetStatusAsync(cancellationToken).ConfigureAwait(false),
            "backup" => await service.BackupAsync(parsed.Path!, cancellationToken).ConfigureAwait(false),
            "verify" => await service.VerifyAsync(parsed.Path!, cancellationToken).ConfigureAwait(false),
            "restore" => await service.RestoreAsync(parsed.Path!, cancellationToken).ConfigureAwait(false),
            "compact" => await service.CompactAsync(cancellationToken).ConfigureAwait(false),
            "prune" => await DatabasePruneCommand.RunAsync(service.DatabasePath, profile, parsed.OlderThanDays!.Value, parsed.Apply, cancellationToken).ConfigureAwait(false),
            _ => throw new DatabaseMaintenanceException("database_command_invalid"),
        };
    }

    private static ParsedDatabaseCommand Parse(string[] args)
    {
        if (args.Length == 0) return ParsedDatabaseCommand.Invalid("database_command_required");
        var operation = args[0];
        if (operation is not ("status" or "backup" or "verify" or "restore" or "compact" or "prune"))
            return ParsedDatabaseCommand.Invalid("database_command_invalid");
        string? dataRoot = null;
        string? path = null;
        int? age = null;
        var apply = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            var canonical = option is "--out" or "--output" or "--path" ? "path" : option;
            if (option.StartsWith("-", StringComparison.Ordinal))
            {
                if (!seen.Add(canonical)) return ParsedDatabaseCommand.Invalid("database_duplicate_argument");
                if (option == "--json") continue;
                if (option == "--apply") { apply = true; continue; }
                if (option is not ("--out" or "--output" or "--path" or "--data-root" or "--older-than-days"))
                    return ParsedDatabaseCommand.Invalid("database_argument_invalid");
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                    return ParsedDatabaseCommand.Invalid("database_argument_value_missing");
                var value = args[index];
                if (option == "--data-root") dataRoot = value;
                else if (option == "--older-than-days")
                {
                    if (!int.TryParse(value, out var parsedAge) || parsedAge is < 1 or > 36500)
                        return ParsedDatabaseCommand.Invalid("database_prune_age_invalid");
                    age = parsedAge;
                }
                else path = value;
            }
            else if ((operation is "verify" or "restore") && seen.Add("path")) path = option;
            else return ParsedDatabaseCommand.Invalid("database_argument_invalid");
        }
        if ((operation is "backup" or "verify" or "restore") && string.IsNullOrWhiteSpace(path))
            return ParsedDatabaseCommand.Invalid("database_path_required");
        if (operation is "status" or "compact" or "prune" && path is not null)
            return ParsedDatabaseCommand.Invalid("database_argument_invalid");
        if (operation == "prune" && age is null) return ParsedDatabaseCommand.Invalid("database_prune_age_required");
        if (operation != "prune" && (age is not null || apply))
            return ParsedDatabaseCommand.Invalid("database_argument_invalid");
        return new ParsedDatabaseCommand(operation, dataRoot, path, age, apply, null);
    }

    private static DataPaths ResolveProfile(string? explicitRoot)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) profile = Directory.GetCurrentDirectory();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = profile;
        var root = explicitRoot
            ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        return DataPaths.Resolve(Directory.GetCurrentDirectory(), profile, local, root);
    }

    private static void WriteResult(object result) => Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));

    private static void WriteError(string errorCode) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error_code = errorCode }, JsonOptions));

    private sealed record ParsedDatabaseCommand(
        string Operation,
        string? DataRoot,
        string? Path,
        int? OlderThanDays,
        bool Apply,
        string? Error)
    {
        public static ParsedDatabaseCommand Invalid(string error) => new("", null, null, null, false, error);
    }
}
