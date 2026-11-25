using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;

namespace RuApp.Services;

/// <summary>
/// Draws Mermaid diagrams, TeX and workspace SVG as pictures (ADR 0057). One hidden
/// WebView2 controller, owned by the main window and never shown, loads the vendored
/// page from <c>Assets/render</c>; the app shows the PNG it returns with native controls.
///
/// The web view is locked down: it may fetch only from its own virtual host, navigate
/// only to its own page, open no window, start no download and grant no permission.
/// Model and workspace text reaches it only as a JSON string in a web message, and its
/// reply is read as untrusted data (<see cref="SandboxRenderPolicy"/>). It is
/// presentation only: no tool, grant or Broker path runs through it. Any failure (no
/// WebView2 runtime, a timeout, a crashed page) returns null and the caller keeps
/// showing the source.
/// </summary>
public sealed class SandboxRenderer
{
    public static SandboxRenderer Shared { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();
    private readonly LinkedList<(string Key, SandboxPicture Picture)> _cache = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, SandboxPicture Picture)>> _cacheIndex = new(StringComparer.Ordinal);
    private CoreWebView2Controller? _controller;
    private TaskCompletionSource<bool>? _ready;
    private long _nextId;
    private bool _unavailable;
    private bool _shutdown;

    private SandboxRenderer()
    {
    }

    /// <summary>
    /// Renders <paramref name="source"/> or returns null. A picture drawn before with the
    /// same source and style comes from the cache without touching the web view.
    /// </summary>
    public async Task<SandboxPicture?> RenderAsync(SandboxRenderKind kind, string source, SandboxRenderStyle style,
        CancellationToken cancellationToken = default)
    {
        if (_unavailable || _shutdown || !SandboxRenderPolicy.Accepts(kind, source)) return null;
        string key = SandboxRenderPolicy.CacheKey(kind, source, style);
        if (TryCached(key, out var cached)) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (TryCached(key, out cached)) return cached;
            if (!await EnsureReadyAsync().ConfigureAwait(true) || _controller is null) return null;

            long id = Interlocked.Increment(ref _nextId);
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            try
            {
                _controller.CoreWebView2.PostWebMessageAsJson(SandboxRenderPolicy.Request(id, kind, source, style));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SandboxRenderPolicy.RenderTimeout);
                string json = await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
                var picture = SandboxRenderPolicy.ReadResult(json, id);
                if (picture is not null) Remember(key, picture);
                return picture;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A page that stops answering is replaced; the next render loads a fresh one.
                GuiDiagnosticWriter.AppendTraceLine("[SandboxRenderer] render timed out");
                Reset();
                return null;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[SandboxRenderer] render failed: {error.GetType().Name}");
            Reset();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the web view; called when the app closes.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        Reset();
    }

    private bool TryCached(string key, out SandboxPicture? picture)
    {
        lock (_cache)
        {
            if (_cacheIndex.TryGetValue(key, out var node))
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                picture = node.Value.Picture;
                return true;
            }
        }

        picture = null;
        return false;
    }

    private void Remember(string key, SandboxPicture picture)
    {
        lock (_cache)
        {
            if (_cacheIndex.ContainsKey(key)) return;
            _cacheIndex[key] = _cache.AddFirst((key, picture));
            while (_cache.Count > SandboxRenderPolicy.MaxCachedPictures && _cache.Last is { } last)
            {
                _cacheIndex.Remove(last.Value.Key);
                _cache.RemoveLast();
            }
        }
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (_ready is not null) return await _ready.Task.ConfigureAwait(true);
        if (MainWindow.Instance is not { } window) return false;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            nint owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var environment = await CoreWebView2Environment.CreateAsync();
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)owner));
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            Lock(core, environment);
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += (_, _) => Reset();
            _controller = controller;
            core.Navigate(SandboxRenderPolicy.PageUrl);
            using var timeout = new CancellationTokenSource(SandboxRenderPolicy.LoadTimeout);
            return await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // No WebView2 runtime, or the page never said it was ready: stay on source.
            GuiDiagnosticWriter.AppendTraceLine($"[SandboxRenderer] unavailable: {error.GetType().Name}");
            _unavailable = true;
            Reset();
            return false;
        }
    }

    private static void Lock(CoreWebView2 core, CoreWebView2Environment environment)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsWebMessageEnabled = true;
        core.SetVirtualHostNameToFolderMapping(
            SandboxRenderPolicy.HostName,
            Path.Combine(AppContext.BaseDirectory, "Assets", "render"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        // Everything but the renderer's own files is refused before it leaves the process.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!SandboxRenderPolicy.MayLoad(args.Request.Uri))
            {
                args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            }
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!SandboxRenderPolicy.IsRendererPage(args.Uri)) args.Cancel = true;
        };
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!SandboxRenderPolicy.IsRendererPage(args.Source)) return;
        string json = args.WebMessageAsJson;
        if (SandboxRenderPolicy.IsReady(json))
        {
            _ready?.TrySetResult(true);
            return;
        }

        if (SandboxRenderPolicy.ResultId(json) is long id && _pending.TryGetValue(id, out var reply))
        {
            reply.TrySetResult(json);
        }
    }

    /// <summary>Drops the web view and fails waiting renders; the next render starts a fresh one.</summary>
    private void Reset()
    {
        var controller = _controller;
        _controller = null;
        _ready?.TrySetResult(false);
        _ready = null;
        foreach (var reply in _pending.Values) reply.TrySetCanceled();
        _pending.Clear();
        if (controller is null) return;
        try
        {
            controller.Close();
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[SandboxRenderer] close failed: {error.GetType().Name}");
        }
    }
}
