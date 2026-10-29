using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.LanguageServers;

/// <summary>Owns configured LSP processes for one canonical workspace and stops their process trees with the Broker.</summary>
public sealed class LanguageServerHost : IAsyncDisposable
{
    public const int MaximumResults = 128;
    public const int MaximumResultBytes = 512 * 1024;
    private readonly string _root;
    private readonly Func<LanguageServerConfiguration> _configuration;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    private sealed class Session(LanguageServerDefinition definition, Process process, WindowsJobProcessTree tree, LspConnection connection, Task stderrDrain)
    {
        public LanguageServerDefinition Definition { get; } = definition;
        public Process Process { get; } = process;
        public WindowsJobProcessTree Tree { get; } = tree;
        public LspConnection Connection { get; } = connection;
        public Task StderrDrain { get; } = stderrDrain;
        public int Version;
        public string? CurrentUri;
        public int CurrentVersion;
        public HashSet<string> OpenDocuments { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim RequestGate { get; } = new(1, 1);
        public bool SupportsSymbols;
        public bool SupportsDefinition;
        public bool SupportsReferences;
        public bool SupportsPullDiagnostics;
        public int SyncKind;
        public bool SupportsOpenClose;
        public JsonObject? Diagnostics;
        public TaskCompletionSource<JsonObject>? DiagnosticsWaiter;
    }

    public LanguageServerHost(string workspaceRoot, Func<string?>? userHome = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        _root = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        _configuration = userHome is null ? () => LanguageServerConfiguration.CurrentUser : () => LanguageServerConfiguration.Load(userHome());
        _environment = environment ?? new AgentCommandEnvironment().ChildEnvironment;
    }

    public async Task<JsonObject> ExecuteAsync(string operation, string language, string path, int? line, int? character, bool allowProtectedPaths, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (language is not ("csharp" or "typescript")) throw new LspException("lsp_language_unsupported");
        var resolved = new CanonicalPathResolver(_root, allowProtectedPaths).Resolve(path);
        if (!resolved.IsInsideWorkspace || !File.Exists(resolved.CanonicalPath)) throw new LspException("lsp_path_denied");
        var session = await GetSessionAsync(language, cancellationToken).ConfigureAwait(false);
        await session.RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var contentBytes = await ReadDocumentAsync(resolved.CanonicalPath, cancellationToken).ConfigureAwait(false);
        string content;
        try { content = new UTF8Encoding(false, true).GetString(contentBytes); }
        catch (DecoderFallbackException) { throw new LspException("lsp_document_not_utf8"); }
        var hash = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        var uri = new Uri(resolved.CanonicalPath).AbsoluteUri;
        if (session.SyncKind == 0 || !session.SupportsOpenClose
            || operation == "symbols" && !session.SupportsSymbols || operation == "definition" && !session.SupportsDefinition
            || operation == "references" && !session.SupportsReferences
            || operation == "diagnostics" && !session.SupportsPullDiagnostics && session.SyncKind == 0)
            throw new LspException("lsp_method_unsupported");
        var version = Interlocked.Increment(ref session.Version);
        var languageId = language == "csharp" ? "csharp" : "typescript";
        session.CurrentUri = uri;
        session.CurrentVersion = version;
        var waiter = operation == "diagnostics"
            ? new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        session.DiagnosticsWaiter = waiter;
        if (session.OpenDocuments.Add(uri))
            await session.Connection.NotifyAsync("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject
            { ["uri"] = uri, ["languageId"] = languageId, ["version"] = version, ["text"] = content } }, cancellationToken).ConfigureAwait(false);
        else if (session.SyncKind == 1)
            await session.Connection.NotifyAsync("textDocument/didChange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = content }) }, cancellationToken).ConfigureAwait(false);
        else
        {
            await session.Connection.NotifyAsync("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, cancellationToken).ConfigureAwait(false);
            session.OpenDocuments.Remove(uri);
            await session.Connection.NotifyAsync("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject
            { ["uri"] = uri, ["languageId"] = languageId, ["version"] = version, ["text"] = content } }, cancellationToken).ConfigureAwait(false);
            session.OpenDocuments.Add(uri);
        }

        JsonNode? result;
        if (operation == "diagnostics")
        {
            JsonObject diagnostics;
            try
            {
                if (session.SupportsPullDiagnostics)
                {
                    var pull = await session.Connection.RequestAsync("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, cancellationToken).ConfigureAwait(false);
                    diagnostics = pull is JsonObject report ? new JsonObject { ["version"] = version, ["diagnostics"] = report["items"]?.DeepClone() ?? new JsonArray() }
                        : throw new LspException("lsp_protocol_error");
                }
                else diagnostics = await waiter!.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { throw new LspException("lsp_diagnostics_timeout"); }
            finally { if (ReferenceEquals(session.DiagnosticsWaiter, waiter)) session.DiagnosticsWaiter = null; }
            if (diagnostics["version"] is not JsonValue diagnosticVersionNode
                || !diagnosticVersionNode.TryGetValue<int>(out var diagnosticVersion) || diagnosticVersion != version)
                throw new LspException("lsp_stale_diagnostics");
            result = diagnostics["diagnostics"]?.DeepClone() ?? new JsonArray();
        }
        else
        {
            if (operation is not ("symbols" or "definition" or "references") || line is null && (operation is "definition" or "references"))
                throw new LspException("lsp_arguments_invalid");
            var paramsNode = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };
            if (operation is "definition" or "references") paramsNode["position"] = new JsonObject { ["line"] = line, ["character"] = character };
            if (operation == "references") paramsNode["context"] = new JsonObject { ["includeDeclaration"] = true };
            var method = operation switch { "symbols" => "textDocument/documentSymbol", "definition" => "textDocument/definition", _ => "textDocument/references" };
            result = await session.Connection.RequestAsync(method, paramsNode, cancellationToken).ConfigureAwait(false);
        }

        var currentBytes = await ReadDocumentAsync(resolved.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(currentBytes), Convert.FromHexString(hash)))
            throw new LspException("lsp_stale_document");
        var bounded = BoundResults(result);
        foreach (var item in bounded.Items) ValidateAndNormalizeUris(item, _root, allowProtectedPaths);
        return new JsonObject { ["language"] = language, ["path"] = Path.GetRelativePath(_root, resolved.CanonicalPath).Replace('\\', '/'),
            ["content_sha256"] = hash, ["version"] = version, ["items"] = bounded.Items, ["truncated"] = bounded.Truncated };
        }
        finally { session.RequestGate.Release(); }
    }

    private static async Task<byte[]> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new LspException("lsp_path_denied");
        if (info.Length > LspFraming.MaximumMessageBytes) throw new LspException("lsp_document_too_large");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream((int)info.Length);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > LspFraming.MaximumMessageBytes) throw new LspException("lsp_document_too_large");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private static (JsonArray Items, bool Truncated) BoundResults(JsonNode? result)
    {
        var items = new JsonArray();
        var values = result is JsonArray array ? array : result is null ? [] : new JsonArray(result.DeepClone());
        var truncated = false;
        foreach (var item in values)
        {
            if (items.Count >= MaximumResults || item is not null
                && Encoding.UTF8.GetByteCount(items.ToJsonString()) + Encoding.UTF8.GetByteCount(item.ToJsonString()) > MaximumResultBytes)
            { truncated = true; break; }
            items.Add(item?.DeepClone());
        }
        if (values.Count > items.Count) truncated = true;
        return (items, truncated);
    }

    private static void ValidateAndNormalizeUris(JsonNode? node, string root, bool allowProtectedPaths)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (key is "uri" or "targetUri")
                {
                    if (value is not JsonValue uriValue || !uriValue.TryGetValue<string>(out var uriText)
                        || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
                        throw new LspException("lsp_external_location_denied");
                    ResolvedPath resolved;
                    try { resolved = new CanonicalPathResolver(root, allowProtectedPaths).Resolve(uri.LocalPath); }
                    catch (ArgumentException) { throw new LspException("lsp_external_location_denied"); }
                    if (!resolved.IsInsideWorkspace) throw new LspException("lsp_external_location_denied");
                    obj[key] = Path.GetRelativePath(root, resolved.CanonicalPath).Replace('\\', '/');
                }
                else ValidateAndNormalizeUris(value, root, allowProtectedPaths);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) ValidateAndNormalizeUris(item, root, allowProtectedPaths);
    }

    private async Task<Session> GetSessionAsync(string language, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var definition = _configuration().Find(language) ?? throw new LspException("lsp_server_not_configured");
            if (_sessions.TryGetValue(language, out var existing))
            {
                if (existing.Process.HasExited || !SameDefinition(existing.Definition, definition)) { await StopAsync(existing).ConfigureAwait(false); _sessions.Remove(language); }
                else return existing;
            }
            var start = new ProcessStartInfo(definition.Command) { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in definition.Arguments) start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var (name, value) in _environment) start.Environment[name] = value;
            var tree = WindowsJobProcessTree.Create();
            Process? process = null;
            LspConnection? connection = null;
            try
            {
                process = Process.Start(start) ?? throw new LspException("lsp_server_unavailable");
                tree.Assign(process.Id);
                connection = new LspConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
                var session = new Session(definition, process, tree, connection, DrainStderrAsync(process.StandardError));
                connection.NotificationReceived += message => OnNotification(session, message);
                var init = await connection.RequestAsync("initialize", new JsonObject { ["processId"] = Environment.ProcessId,
                    ["rootUri"] = new Uri(_root).AbsoluteUri, ["capabilities"] = new JsonObject
                    { ["textDocument"] = new JsonObject { ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false } } }, ["workspaceFolders"] = new JsonArray(
                        new JsonObject { ["uri"] = new Uri(_root).AbsoluteUri, ["name"] = Path.GetFileName(_root) }) }, cancellationToken).ConfigureAwait(false);
                if (init is not JsonObject initialized || initialized["capabilities"] is not JsonObject capabilities || process.HasExited)
                    throw new LspException("lsp_initialization_failed");
                session.SupportsSymbols = Supports(capabilities["documentSymbolProvider"]);
                session.SupportsDefinition = Supports(capabilities["definitionProvider"]);
                session.SupportsReferences = Supports(capabilities["referencesProvider"]);
                session.SupportsPullDiagnostics = Supports(capabilities["diagnosticProvider"]);
                session.SyncKind = capabilities["textDocumentSync"] switch
                {
                    JsonValue value when value.TryGetValue<int>(out var kind) => kind,
                    JsonObject sync when sync["change"] is JsonValue change && change.TryGetValue<int>(out var kind) => kind,
                    _ => 0,
                };
                session.SupportsOpenClose = capabilities["textDocumentSync"] is JsonObject syncOptions
                    ? ReadBoolean(syncOptions["openClose"])
                    : session.SyncKind > 0;
                await connection.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
                _sessions.Add(language, session);
                return session;
            }
            catch
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                if (process is not null) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } process.Dispose(); }
                tree.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new LspException("lsp_server_unavailable"); }
        finally { _gate.Release(); }
    }

    private static bool Supports(JsonNode? value) => value switch
    {
        JsonValue json when json.TryGetValue<bool>(out var enabled) => enabled,
        JsonObject => true,
        JsonArray => true,
        _ => false,
    };

    private static bool ReadBoolean(JsonNode? value) => value is JsonValue json && json.TryGetValue<bool>(out var result) && result;

    private static bool SameDefinition(LanguageServerDefinition left, LanguageServerDefinition right) =>
        string.Equals(left.Command, right.Command, StringComparison.OrdinalIgnoreCase)
        && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

    private static void OnNotification(Session session, JsonObject message)
    {
        if (message["method"] is not JsonValue methodNode || !methodNode.TryGetValue<string>(out var method)
            || method != "textDocument/publishDiagnostics" || message["params"] is not JsonObject parameters) return;
        if (parameters["uri"] is not JsonValue uriNode || !uriNode.TryGetValue<string>(out var uri)
            || !string.Equals(uri, session.CurrentUri, StringComparison.Ordinal)
            || parameters["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var version)
            || version != session.CurrentVersion) return;
        session.Diagnostics = parameters.DeepClone().AsObject();
        session.DiagnosticsWaiter?.TrySetResult(session.Diagnostics.DeepClone().AsObject());
    }

    private static async Task StopAsync(Session session)
    {
        await session.Connection.DisposeAsync().ConfigureAwait(false);
        try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        session.Process.Dispose();
        session.Tree.Dispose();
        try { await session.StderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        session.RequestGate.Dispose();
    }

    private static async Task DrainStderrAsync(StreamReader stderr)
    {
        var buffer = new char[2048];
        try { while (await stderr.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { foreach (var session in _sessions.Values) await StopAsync(session).ConfigureAwait(false); _sessions.Clear(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var session in _sessions.Values)
        {
            try { session.Connection.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (ObjectDisposedException) { }
            try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            session.Process.Dispose();
            session.Tree.Dispose();
            try { session.StderrDrain.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            session.RequestGate.Dispose();
        }
        _sessions.Clear();
        _gate.Dispose();
    }
}
