using System.Net.Http.Headers;
using System.Text.Json;
using RuAgent.Updates;

namespace RuAgent.Client;

/// <summary>Stops an idle local host only after it advertises the atomic maintenance protocol.</summary>
public static class UpdateMaintenance
{
    public static async Task PrepareAsync(CancellationToken ct, PublicHostLocator? locator = null, HttpMessageHandler? handler = null)
    {
        locator ??= new PublicHostLocator();
        if (!locator.TryGetValidDescriptor(out var descriptor, out var error))
        {
            if (error is "not_found" or "stale_pid") return;
            throw new UpdateException("update_host_state_unknown");
        }
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", descriptor!.BearerToken);
        var root = new Uri(descriptor.BaseUrl);
        using var status = await http.GetAsync(new Uri(root, "/status"), ct).ConfigureAwait(false);
        status.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("maintenance_protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var value) || value != 1)
            throw new UpdateException("update_host_protocol_unsupported");
        // Polling clients may briefly occupy the admission gate; retries are bounded.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var response = await http.PostAsync(new Uri(root, "/host/shutdown?when_idle=true"), null, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && response.Headers.TryGetValues("X-Ru-Update-Ready", out var ready) && ready.SingleOrDefault() == "1") return;
            if ((int)response.StatusCode != 409) throw new UpdateException("update_host_prepare_failed");
            if (attempt < 2) await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new UpdateException("update_busy");
    }
}
