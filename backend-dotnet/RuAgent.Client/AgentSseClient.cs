using System.Net.Http.Headers;
using System.Globalization;
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
    private const int MaxSseFrameDataBytes = 512 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly PublicHostSession _session;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private bool _disposed;

    public AgentSseClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                // An SSE connection is intentionally long-lived; cancellation is
                // controlled by the caller token rather than an arbitrary request
                // timeout.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttpClient = true;
        }
        else
        {
            // Callers commonly share one client between API and SSE adapters. Do not
            // mutate its timeout or dispose it when this short-lived stream wrapper ends.
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public async IAsyncEnumerable<SseFrame> ReadEventsAsync(
        string runId,
        long afterSequence = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);

        string? currentId = null;
        string? currentEvent = null;
        var dataBuilder = new StringBuilder();
        var dataBytes = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DecoderFallbackException ex)
            {
                throw new AgentApiException("invalid_sse_data", ex.Message);
            }

            if (line is null) break;

            if (line.Length > MaxSseFrameDataBytes)
            {
                throw new AgentApiException("sse_frame_too_large", "SSE frame is too large.");
            }

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
                dataBytes = 0;
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

            if (field is "data" && dataBytes > MaxSseFrameDataBytes - StrictUtf8.GetByteCount(value) - 1)
            {
                throw new AgentApiException("sse_frame_too_large", "SSE frame is too large.");
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
                    dataBytes += StrictUtf8.GetByteCount(value) + 1;
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
                if (!long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var tseq) || tseq < 0)
                    throw new AgentApiException("invalid_sse_data", "Transient SSE id is invalid.");

                transientSeq = tseq;
            }
            else
            {
                if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var dseq) || dseq <= 0)
                    throw new AgentApiException("invalid_sse_data", "Durable SSE id is invalid.");

                durableSeq = dseq;
            }
        }

        if (!string.IsNullOrWhiteSpace(eventType) && eventType == "transient")
            isTransient = true;

        if (isTransient)
        {
            if (!transientSeq.HasValue && !string.IsNullOrWhiteSpace(data))
            {
                throw new AgentApiException("invalid_sse_data", "Transient SSE id is required.");
            }

            // A transient event can never advance the durable cursor, even if a
            // malformed sender supplied a numeric id alongside event: transient.
            durableSeq = null;
        }
        else if (!durableSeq.HasValue
            && !string.IsNullOrWhiteSpace(data)
            && !string.Equals(eventType, "final_snapshot", StringComparison.Ordinal))
        {
            throw new AgentApiException("invalid_sse_data", "Durable SSE id is required.");
        }

        if (!isTransient && string.Equals(eventType, "final_snapshot", StringComparison.Ordinal))
        {
            try
            {
                using var finalDocument = JsonDocument.Parse(data);
                if (finalDocument.RootElement.ValueKind != JsonValueKind.Object
                    || !finalDocument.RootElement.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(status.GetString())
                    || status.GetString()!.Length > 64)
                {
                    throw new AgentApiException("invalid_sse_data", "Final snapshot is invalid.");
                }
            }
            catch (AgentApiException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new AgentApiException("invalid_sse_data", ex.Message);
            }
        }

        // For transient, durableSequence remains null (does not advance cursor)
        // For durable, validate sequence non-negative and data is valid JSON envelope with protocol_version check
        if (!isTransient && durableSeq.HasValue)
        {
            // Strict: try to parse data as HarnessSafeEventEnvelope and check version
            try
            {
                var doc = JsonDocument.Parse(data);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("protocol_version", out var v)
                    || !v.TryGetInt32(out var ver))
                {
                    throw new AgentApiException("invalid_sse_data", "Durable SSE envelope is missing protocol_version.");
                }

                if (ver != HarnessSafeEventEnvelope.CurrentProtocolVersion)
                {
                    throw new AgentApiException("unsupported_protocol_version", $"unsupported version {ver}");
                }
            }
            catch (AgentApiException)
            {
                throw;
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
        if (string.IsNullOrWhiteSpace(runId)
            || !runId.StartsWith("run_", StringComparison.Ordinal)
            || runId.Length != 36
            || runId.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new AgentApiException("invalid_run_id", "run_id invalid");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
