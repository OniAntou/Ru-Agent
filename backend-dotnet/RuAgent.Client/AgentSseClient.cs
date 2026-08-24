using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed record SseFrame(
    long? DurableSequence,
    string EventType,
    string Data,
    bool IsTransient,
    long? TransientSequence);

public sealed class AgentSseClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;
    private bool _disposed;

    public AgentSseClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async IAsyncEnumerable<SseFrame> ReadEventsAsync(
        string runId,
        long afterSequence = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        if (afterSequence < 0) throw new AgentApiException("invalid_cursor", "after_sequence must be non-negative");

        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/events?after_sequence={afterSequence}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSequence > 0)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", afterSequence.ToString());

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new AgentApiException("sse_failed", body, (int)response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentId = null;
        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;

            if (string.IsNullOrEmpty(line))
            {
                // Dispatch event
                if (dataBuilder.Length > 0 || currentId is not null)
                {
                    var data = dataBuilder.ToString().TrimEnd('\n');
                    var frame = ParseFrame(currentId, currentEvent, data);
                    if (frame is not null)
                        yield return frame;
                }
                currentId = null;
                currentEvent = null;
                dataBuilder.Clear();
                continue;
            }

            if (line.StartsWith(":"))
            {
                // Comment/keepalive, ignore
                continue;
            }

            var colon = line.IndexOf(':');
            string field, value;
            if (colon < 0)
            {
                field = line.Trim();
                value = string.Empty;
            }
            else
            {
                field = line.Substring(0, colon).Trim();
                value = line.Substring(colon + 1).TrimStart(' ');
            }

            switch (field)
            {
                case "id":
                    currentId = value;
                    break;
                case "event":
                    currentEvent = value;
                    break;
                case "data":
                    dataBuilder.AppendLine(value);
                    break;
            }
        }
    }

    private static SseFrame? ParseFrame(string? id, string? eventType, string data)
    {
        if (string.IsNullOrWhiteSpace(data) && string.IsNullOrWhiteSpace(id)) return null;

        bool isTransient = false;
        long? durableSeq = null;
        long? transientSeq = null;

        if (!string.IsNullOrWhiteSpace(id))
        {
            if (id.StartsWith("t", StringComparison.Ordinal))
            {
                isTransient = true;
                if (long.TryParse(id.AsSpan(1), out var tseq))
                    transientSeq = tseq;
            }
            else
            {
                if (long.TryParse(id, out var dseq))
                    durableSeq = dseq;
            }
        }

        if (!string.IsNullOrWhiteSpace(eventType) && eventType == "transient")
            isTransient = true;

        // For transient, durableSequence remains null (does not advance cursor)
        // For durable, validate sequence non-negative and data is valid JSON envelope with protocol_version check
        if (!isTransient && durableSeq.HasValue)
        {
            // Strict: try to parse data as HarnessSafeEventEnvelope and check version
            try
            {
                var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("protocol_version", out var v) && v.TryGetInt32(out var ver))
                {
                    if (ver != HarnessSafeEventEnvelope.CurrentProtocolVersion)
                        throw new AgentApiException("unsupported_protocol_version", $"unsupported version {ver}");
                }
            }
            catch (JsonException ex)
            {
                throw new AgentApiException("invalid_sse_data", ex.Message);
            }
        }

        return new SseFrame(durableSeq, eventType ?? (isTransient ? "transient" : "message"), data, isTransient, transientSeq);
    }

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || !runId.StartsWith("run_", StringComparison.Ordinal) || runId.Length != 36)
            throw new AgentApiException("invalid_run_id", "run_id invalid");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
