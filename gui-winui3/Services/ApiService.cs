using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

using RuApp.Services;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services
{
    public sealed record ApiErrorSnapshot(
        string Method,
        int? StatusCode,
        string ErrorType,
        string Message,
        DateTimeOffset OccurredAt);

    public sealed class ApiErrorEventArgs : EventArgs
    {
        public ApiErrorEventArgs(ApiErrorSnapshot error) => Error = error;
        public ApiErrorSnapshot Error { get; }
    }

    public partial class ApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly BackendSession _session;
        private readonly string? _logRoot;

        public ApiService(string authSecret, int port, string? logRoot = null)
            : this(BackendSession.FromLoopback(port, authSecret), logRoot)
        {
        }

        public ApiService(BackendSession session, string? logRoot = null)
            : this(session, handler: null, logRoot)
        {
        }

        internal ApiService(BackendSession session, HttpMessageHandler? handler, string? logRoot = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _baseUrl = session.HttpBaseUri.ToString().TrimEnd('/');
            _logRoot = logRoot;
            _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AuthSecret);
        }

        public event EventHandler? SettingsSaved;
        public event EventHandler<ApiErrorEventArgs>? RequestFailed;

        public ApiErrorSnapshot? LastError { get; private set; }

        public SkillsApiClient CreateSkillsApiClient()
        {
            return new SkillsApiClient(RuAppComposition.ToPublicHostSession(_session), _httpClient);
        }

        public PluginsApiClient CreatePluginsApiClient()
        {
            return new PluginsApiClient(RuAppComposition.ToPublicHostSession(_session), _httpClient);
        }

        private void LogApiError(string method, Exception ex)
        {
            if (IsCancellation(ex))
            {
                return;
            }

            int? statusCode = (ex as HttpRequestException)?.StatusCode.HasValue == true
                ? (int)(ex as HttpRequestException)!.StatusCode!.Value
                : null;

            var snapshot = new ApiErrorSnapshot(
                method,
                statusCode,
                ex.GetType().Name,
                ex.Message,
                DateTimeOffset.UtcNow);

            LastError = snapshot;

            try
            {
                GuiDiagnosticWriter.WriteApiError(method, ex.GetType().Name, statusCode, _logRoot);
            }
            catch
            {
                // Diagnostics must never break the API caller.
            }

            var status = statusCode.HasValue
                ? $" status={statusCode.Value} "
                : string.Empty;
            GuiDiagnosticWriter.AppendTraceLine(
                $"[ApiService] {method} failed: {ex.GetType().Name}{status}: {ex.Message}");
            if (ex.InnerException != null)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ApiService] {method} inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }

            try
            {
                RequestFailed?.Invoke(this, new ApiErrorEventArgs(snapshot));
            }
            catch
            {
                // Prevent subscriber exceptions from breaking caller
            }
        }

        private static bool IsCancellation(Exception ex) =>
            ex is OperationCanceledException || ex is TaskCanceledException;

        /// <summary>
        /// The Host's bounded rejection code (a lowercase slug), or a fixed fallback. Never the
        /// free-text message, which the diagnostic log must not carry.
        /// </summary>
        internal static async Task<string> RejectionCodeAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) return "http_" + (int)response.StatusCode;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.String
                    && code.GetString() is { Length: > 0 and <= 64 } value
                    && value.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                    return value;
            }
            catch (JsonException) { }
            return "rejected_without_code";
        }

        private static async Task<bool> HasOkEnvelopeAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) return false;
            string body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return false;

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String &&
                    (string.Equals(status.GetString(), "ok", StringComparison.Ordinal) ||
                     string.Equals(status.GetString(), "success", StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            RequestFailed = null;
            SettingsSaved = null;
            _httpClient.Dispose();
        }
    }
}
