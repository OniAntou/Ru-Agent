using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The outcome of a provider connection test, ready to show.
/// </summary>
/// <remarks>
/// Tone is a small closed set rather than a brush, so this stays free of XAML and
/// the view decides how each tone looks.
/// </remarks>
internal sealed record ProviderConnectionTest(string Tone, string Message)
{
    internal const string NeutralTone = "neutral";
    internal const string SuccessTone = "success";
    internal const string ErrorTone = "error";
}

/// <summary>
/// Turns a provider catalog result into what a connection test tells the user.
/// </summary>
/// <remarks>
/// A key is stored, not proven. The only way to know it works is to ask the endpoint,
/// which is what /models does, so this reads the catalog's own verdict rather than
/// guessing from the has_*_key flags.
///
/// Kept out of the view so every branch is asserted: these messages are the entire
/// feedback a user gets, and a branch that fell through to a blank line would look
/// like the test did nothing.
/// </remarks>
internal static class ProviderConnectionTestPresentation
{
    internal const string ButtonLabel = "Test connection";
    internal const string TestingMessage = "Testing…";

    /// <summary>The message shown while the request is in flight.</summary>
    internal static ProviderConnectionTest Testing() =>
        new(ProviderConnectionTest.NeutralTone, TestingMessage);

    /// <summary>
    /// Reads a finished catalog result as a test outcome.
    /// </summary>
    internal static ProviderConnectionTest Project(ProviderCatalogState? state)
    {
        if (state is null)
        {
            return new ProviderConnectionTest(
                ProviderConnectionTest.ErrorTone,
                "Could not reach the backend to test this provider.");
        }

        if (state.IsSuccess)
        {
            // A reachable endpoint that lists nothing is a real answer, not a failure:
            // a local server with no models loaded behaves this way.
            return new ProviderConnectionTest(
                ProviderConnectionTest.SuccessTone,
                state.Models.Count switch
                {
                    0 => "The endpoint answered, but it lists no models. Load one before sending a message.",
                    1 => "Connection works. 1 model available.",
                    _ => $"Connection works. {state.Models.Count} models available.",
                });
        }

        return new ProviderConnectionTest(
            ProviderConnectionTest.ErrorTone,
            MessageFor(state.ErrorCode));
    }

    /// <summary>
    /// Whether a refresh that just failed is worth reporting as a connection test.
    /// </summary>
    /// <remarks>
    /// The catalog also refreshes on a timer. Without this, an unrelated background
    /// failure while the settings pane is open would land in the test's status line,
    /// as if the user had asked for it.
    /// </remarks>
    internal static bool ReportsFailure(ProviderConnectionTest? test) =>
        test is { Tone: ProviderConnectionTest.ErrorTone };

    internal static string? DiagnosticCode(ProviderConnectionTest test, ProviderCatalogState? state) =>
        test.Tone == ProviderConnectionTest.ErrorTone
            ? state?.ErrorCode ?? "unknown"
            : null;

    /// <summary>
    /// Copy for one catalog error code.
    /// </summary>
    /// <remarks>
    /// The codes are the ones the host's catalog actually produces, taken from
    /// ProviderCatalogService rather than guessed. A live run turned up
    /// catalog_unauthorized, which an earlier version of this table did not know and
    /// so printed the raw code at the user.
    ///
    /// An unmapped code still names itself: it is the only handle a report has.
    /// </remarks>
    private static readonly Dictionary<string, string> CatalogMessages =
        new(StringComparer.Ordinal)
        {
            ["catalog_unauthorized"] =
                "The provider rejected the API key. Check that it is correct, complete, and still active.",
            ["catalog_rate_limited"] =
                "The provider is rate-limiting this key. Wait a moment, then test again.",
            ["catalog_http_error"] =
                "The provider returned an error. Check the key and the selected model.",
            ["catalog_timeout"] =
                "The provider did not answer in time. Check your network connection and try again.",
            ["catalog_too_large"] =
                "The provider's model list was too large to read. Try a different provider.",
            ["catalog_malformed"] =
                "The provider answered in a shape Ru could not read. Check the endpoint URL.",
            ["provider_not_active"] =
                "No provider is selected. Choose one above, then test again.",
            // The host folds a rejected credential into this code before it ever
            // reaches the provider.
            ["catalog_configuration_error"] =
                "The provider could not be configured. Check that the API key is correct and complete.",
            ["catalog_unavailable"] =
                "Could not reach the provider. Check your network connection and try again.",
        };

    /// <summary>
    /// The codes this surface phrases. Every one of them is also cataloged in
    /// <see cref="AgentFailureCopy"/>, which owns the run-level wording; a parity test
    /// fails when a new code is phrased here without being catalogsed there.
    /// </summary>
    internal static IReadOnlyCollection<string> PhrasedCodes => CatalogMessages.Keys;

    private static string MessageFor(string? errorCode)
    {
        string code = errorCode?.Trim() ?? string.Empty;
        if (CatalogMessages.TryGetValue(code, out var message))
        {
            return message;
        }

        // An unmapped code still names itself: it is the only handle a report has.
        return code.Length == 0
            ? CatalogMessages["catalog_unavailable"]
            : $"The provider rejected the request ({errorCode}).";
    }
}
