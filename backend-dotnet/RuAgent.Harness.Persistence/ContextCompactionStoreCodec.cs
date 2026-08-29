using System.Text.Json;
using System.Text.Json.Serialization;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence;

internal static class ContextCompactionStoreCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    internal static string SerializeRanges(IReadOnlyList<ContextEntryRange> ranges) =>
        JsonSerializer.Serialize(
            ranges.Select(range => new StoredRange(
                range.SourceId,
                range.StartSequence,
                range.EndSequence)),
            JsonOptions);

    internal static IReadOnlyList<ContextEntryRange> DeserializeRanges(string json)
    {
        try
        {
            var ranges = JsonSerializer.Deserialize<StoredRange[]>(json, JsonOptions);
            if (ranges is null || ranges.Length == 0 || ranges.Any(range => range is null))
            {
                throw new InvalidDataException("Context compaction ranges are missing.");
            }

            return ranges
                .Select(range => new ContextEntryRange(
                    range.SourceId,
                    range.StartSequence,
                    range.EndSequence))
                .ToArray();
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            throw new HarnessStoreException("context_compaction_corrupt", exception);
        }
    }

    private sealed record StoredRange(
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("start_sequence")] long StartSequence,
        [property: JsonPropertyName("end_sequence")] long EndSequence);
}
