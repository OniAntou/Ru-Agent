namespace RuBackend.Business.Activity;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Diagnostics;

public sealed class ActivityLogMigrationService
{
    private readonly string _logFile;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public ActivityLogMigrationService(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logFile = paths.ActivityLogFile;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_logFile))
        {
            return;
        }

        var parent = Path.GetDirectoryName(_logFile);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        var temporaryFile = Path.Combine(parent, $".{Path.GetFileName(_logFile)}.{Guid.NewGuid():N}.migration.tmp");

        try
        {
            await using (var sourceStream = new FileStream(
                _logFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                useAsync: true))
            using (var reader = new BoundedTextLineReader(new StreamReader(sourceStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true), 256_000))
            {
                await using (var targetStream = new FileStream(
                    temporaryFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true))
                {
                    await using (var writer = new StreamWriter(targetStream, new UTF8Encoding(false)))
                    {
                        while (true)
                        {
                            var read = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                            if (read.EndOfFile) break;
                            if (read.TooLarge) continue;
                            var line = read.Line!;
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                continue;
                            }

                            try
                            {
                                if (JsonNode.Parse(line) is JsonObject root)
                                {
                                    var ts = root.TryGetPropertyValue("ts", out var tsv) && tsv is JsonValue tv && tv.TryGetValue<double>(out var t) ? t : 0;
                                    var iso = root.TryGetPropertyValue("iso", out var isov) && isov is JsonValue isv ? isv.GetValue<string>() : string.Empty;
                                    var type = root.TryGetPropertyValue("type", out var typev) && typev is JsonValue tyv ? tyv.GetValue<string>() : "chat";
                                    var eventId = root.TryGetPropertyValue("event_id", out var eidv) && eidv is JsonValue eiv ? eiv.GetValue<string>() : null;
                                    var sessionId = root.TryGetPropertyValue("session_id", out var sidv) && sidv is JsonValue sv ? sv.GetValue<string>() : null;
                                    var data = root.TryGetPropertyValue("data", out var datav) && datav is JsonObject d ? d : new JsonObject();

                                    var evt = new ActivityEvent(eventId, ts, iso, type, sessionId, data);
                                    var sanitized = ActivityPreview.SanitizeEvent(evt);
                                    await writer.WriteLineAsync(JsonSerializer.Serialize(sanitized, JsonOptions))
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                            {
                                // Skip malformed line
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            File.Move(temporaryFile, _logFile, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not break startup if migration fails
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                try { File.Delete(temporaryFile); } catch { }
            }
        }
    }
}
