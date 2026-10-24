using System.Net;
using System.Text.Json;

namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Separates explicit account limits from throttling without echoing provider bodies.</summary>
internal static class ModelRateLimitError
{
    private const int MaximumBodyBytes = 16 * 1024;

    internal static async Task<ModelProviderException> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
            throw new ArgumentException("Expected HTTP 429.", nameof(response));
        var retryAfter = GetRetryAfter(response);
        if (response.Content.Headers.ContentLength > MaximumBodyBytes) return Throttle(retryAfter);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[MaximumBodyBytes + 1];
            var used = 0;
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(used), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                used += count;
                if (used > MaximumBodyBytes) return Throttle(retryAfter);
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object) return Throttle(retryAfter);

            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                if (IsQuotaCode(code.GetString()))
                    return new("quota_exhausted", retryable: false);
                return Throttle(retryAfter); // A specific code takes priority over a broader type.
            }
            if (code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "insufficient_quota")
                return new("quota_exhausted", retryable: false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { }
        token.ThrowIfCancellationRequested();
        return Throttle(retryAfter);
    }

    // Remaining-request headers also describe a rate window; zero alone is not
    // proof of exhausted credit. Unknown/malformed bodies remain throttles.
    private static ModelProviderException Throttle(TimeSpan? retryAfter) => new("rate_limited", retryable: true, retryAfter: retryAfter);

    internal static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return value is { } delay && delay > TimeSpan.Zero
            ? delay
            : null;
    }

    internal static bool IsQuotaCode(string? code) => code is
        "insufficient_quota" or "billing_hard_limit_reached" or "credit_balance_exhausted"
        or "organization_spend_limit_exceeded" or "project_spend_limit_exceeded" or "organization_usage_limit_exceeded";
}
