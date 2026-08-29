using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Runtime;

public sealed record RuntimeSettingsInput
{
    public RuntimeSettingsInput(
        string backend,
        string modelName,
        string? baseUrl,
        string apiVariant,
        bool kiloReasoningEnabled)
    {
        Backend = RequireText(backend, 64, nameof(backend));
        ModelName = RequireText(modelName, RuntimeLimits.MaxModelNameChars, nameof(modelName));
        BaseUrl = baseUrl;
        ApiVariant = RequireText(apiVariant, 64, nameof(apiVariant), allowEmpty: true);
        KiloReasoningEnabled = kiloReasoningEnabled;
    }

    public string Backend { get; }

    public string ModelName { get; }

    public string? BaseUrl { get; }

    public string ApiVariant { get; }

    public bool KiloReasoningEnabled { get; }

    private static string RequireText(
        string? value,
        int maximum,
        string parameterName,
        bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || value.Contains('\0')
            || value.Any(char.IsControl)
            || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Runtime setting is invalid.", parameterName);
        }

        return value.Trim();
    }
}

public interface IManagedRuntimeApplier
{
    Task ApplyAsync(
        RuntimeSettingsSnapshot settings,
        CancellationToken cancellationToken);
}

public sealed class ManagedRuntimeSettingsException : Exception
{
    public ManagedRuntimeSettingsException(
        string code,
        bool retryable,
        Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

public sealed class ManagedRuntimeSettingsService
{
    private static readonly IReadOnlySet<string> SupportedBackends = new HashSet<string>(
        [
            "OpenCode Zen",
            "Kilo Gateway",
            "Ollama",
            "LM Studio",
            "Custom OpenAI",
        ],
        StringComparer.Ordinal);

    private readonly IManagedRuntimeApplier? _applier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RuntimeSettingsSnapshot _current;

    public ManagedRuntimeSettingsService(
        RuntimeSettingsSnapshot initial,
        IManagedRuntimeApplier? applier = null)
    {
        _current = ValidateSnapshot(initial);
        _applier = applier;
    }

    public RuntimeSettingsSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public async Task<string> LoadAsync(
        RuntimeSettingsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var candidate = BuildCandidate(input);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _current;
            try
            {
                if (_applier is not null)
                {
                    await _applier.ApplyAsync(candidate, cancellationToken).ConfigureAwait(false);
                }

                _current = candidate;
                return "Runtime loaded.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _current = previous;
                throw;
            }
            catch (Exception error)
            {
                _current = previous;
                throw new ManagedRuntimeSettingsException(
                    "runtime_load_failed",
                    retryable: false,
                    error);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private RuntimeSettingsSnapshot BuildCandidate(RuntimeSettingsInput input)
    {
        if (!SupportedBackends.Contains(input.Backend))
        {
            throw new ArgumentException("Runtime backend is not supported.", nameof(input));
        }

        var baseUrl = ParseBaseUrl(input.BaseUrl);
        return ValidateSnapshot(new RuntimeSettingsSnapshot(
            input.Backend,
            input.ModelName,
            baseUrl,
            input.ApiVariant,
            input.KiloReasoningEnabled));
    }

    private static RuntimeSettingsSnapshot ValidateSnapshot(RuntimeSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SupportedBackends.Contains(snapshot.Backend)
            || string.IsNullOrWhiteSpace(snapshot.ModelName)
            || snapshot.ModelName.Length > RuntimeLimits.MaxModelNameChars
            || snapshot.ModelName.Contains('\0')
            || snapshot.ModelName.Any(char.IsControl)
            || snapshot.ApiVariant is null
            || snapshot.ApiVariant.Length > 64
            || snapshot.ApiVariant.Contains('\0')
            || snapshot.ApiVariant.Any(char.IsControl))
        {
            throw new ArgumentException("Runtime settings are invalid.", nameof(snapshot));
        }

        if (snapshot.BaseUrl is not null)
        {
            ValidateBaseUrl(snapshot.BaseUrl);
        }

        return snapshot with
        {
            Backend = snapshot.Backend.Trim(),
            ModelName = snapshot.ModelName.Trim(),
            ApiVariant = snapshot.ApiVariant.Trim(),
        };
    }

    private static Uri? ParseBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Runtime endpoint is invalid.", nameof(value));
        }

        ValidateBaseUrl(uri);
        return uri;
    }

    private static void ValidateBaseUrl(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.ToString().Length > 2_048)
        {
            throw new ArgumentException("Runtime endpoint is invalid.", nameof(uri));
        }
    }
}
