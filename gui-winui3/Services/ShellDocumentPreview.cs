using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace RuApp.Services;

/// <summary>Hosts an installed Windows preview handler through its out-of-process surrogate.</summary>
internal sealed class ShellDocumentPreview(PreviewStaWorker worker, nint parent, Action leavePreview) : IDisposable
{
    private object? _instance;
    private IPreviewHandler? _handler;
    private FileStream? _source;
    private nint _host;
    private bool _disposed;
    private int _width, _height;
    private (int X, int Y, int Width, int Height, bool Visible)? _position;
    private readonly nint _parent = parent;

    public async Task OpenAsync(string workspace, string path, CancellationToken ct)
    {
        // PowerPoint's DirectComposition content does not paint beneath WinUI's
        // no-redirection HWND. An owned borderless host has its own DWM surface.
        _host = CreateWindowEx(0x80, "STATIC", "Ru document preview", 0x86000000, 0, 0, 1, 1, _parent, 0, 0, 0);
        if (_host == 0) throw new IOException("preview_host_unavailable");
        nint host = _host;
        await worker.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _source = FilePreviewSource.Open(workspace, path);
                FilePreviewSource.ValidateOfficePackage(_source, path);
                Guid clsid = FindHandler(Path.GetExtension(path));
                Guid iid = typeof(IPreviewHandler).GUID;
                // CLSCTX_LOCAL_SERVER only: never load a third-party preview DLL into Ru.
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 4, ref iid, out var pointer));
                try { _instance = Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
                _handler = (IPreviewHandler)_instance;
                if (_instance is IObjectWithSite site) site.SetSite(new PreviewHandlerSite(_parent, leavePreview));
                if (_instance is IInitializeWithStream stream)
                {
                    Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(Path.GetFullPath(path), 0x20, 0, false, 0, out var input));
                    try { stream.Initialize(input, 0); } finally { Marshal.ReleaseComObject(input); }
                }
                else if (_instance is IInitializeWithFile file) file.Initialize(Path.GetFullPath(path), 0);
                else throw new NotSupportedException("preview_handler_unsupported");
                ct.ThrowIfCancellationRequested();
                var rect = new Rect { Right = Math.Max(1, _width), Bottom = Math.Max(1, _height) };
                _handler.SetWindow(host, ref rect);
                _handler.DoPreview();
                ct.ThrowIfCancellationRequested();
            }
            catch { Unload(); throw; }
        }).WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    public void Position(int x, int y, int width, int height, bool visible)
    {
        if (_host == 0 || _disposed) return;
        visible &= HasForegroundOwner(_parent);
        var screen = new Point { X = x, Y = y };
        if (!ClientToScreen(_parent, ref screen)) visible = false;
        x = screen.X; y = screen.Y;
        var position = (x, y, width, height, visible);
        if (_position == position) return;
        _position = position;
        if (!visible) ShowWindow(_host, 0);
        SetWindowPos(_host, 0, x, y, Math.Max(1, width), Math.Max(1, height), 0x0210);
        if (visible) ShowWindow(_host, 4);
        if (width == _width && height == _height) return;
        _width = width; _height = height;
        _ = ResizeAsync(width, height);
    }

    private async Task ResizeAsync(int width, int height)
    {
        try
        {
            await worker.InvokeAsync(() =>
            {
                var rect = new Rect { Right = Math.Max(1, width), Bottom = Math.Max(1, height) };
                _handler?.SetRect(ref rect);
            });
        }
        catch (Exception e) { GuiDiagnosticWriter.AppendTraceLine($"[Preview] Resize: {e.GetType().Name}"); }
    }

    internal static Guid FindHandler(string extension)
    {
        const string iid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
        using var typeKey = Registry.ClassesRoot.OpenSubKey(extension);
        string? progId = typeKey?.GetValue(null) as string;
        foreach (string location in new[] { $@"{extension}\shellex\{iid}", $@"{progId}\shellex\{iid}", $@"SystemFileAssociations\{extension}\shellex\{iid}" })
        {
            using var key = Registry.ClassesRoot.OpenSubKey(location);
            if (Guid.TryParse(key?.GetValue(null) as string, out var guid)) return guid;
        }
        throw new NotSupportedException("preview_renderer_missing: Install Microsoft Office or a Windows preview handler for this document type.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_host != 0) { ShowWindow(_host, 0); DestroyWindow(_host); _host = 0; }
        _ = UnloadAsync();
    }

    private async Task UnloadAsync()
    {
        try { await worker.InvokeAsync(Unload, cleanup: true); }
        catch (Exception e) { GuiDiagnosticWriter.AppendTraceLine($"[Preview] Unload: {e.GetType().Name}"); }
    }

    public Task FocusAsync(CancellationToken ct)
    {
        if (_disposed || _host == 0) return Task.CompletedTask;
        // Called only by the user's Focus preview action.
        SetForegroundWindow(_host);
        return worker.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); _handler?.SetFocus(); }).WaitAsync(TimeSpan.FromSeconds(3), ct);
    }

    internal static bool HasForegroundOwner(nint owner) => GetAncestor(GetForegroundWindow(), 3) == owner;

    private void Unload()
    {
        try { _handler?.Unload(); }
        catch (COMException) { /* A crashed surrogate must still release the local reference and file. */ }
        finally
        {
            _handler = null;
            try
            {
                if (_instance is not null)
                {
                    try { if (_instance is IObjectWithSite site) site.SetSite(null); }
                    catch (COMException) { /* A disconnected surrogate can no longer hold the site. */ }
                    finally { Marshal.ReleaseComObject(_instance); _instance = null; }
                }
            }
            finally { _source?.Dispose(); _source = null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(nint hwnd, ref Rect rect); void SetRect(ref Rect rect); void DoPreview(); void Unload(); void SetFocus(); void QueryFocus(out nint hwnd); [PreserveSig] int TranslateAccelerator(nint message);
    }
    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        void GetSite(ref Guid iid, out nint site);
    }
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, nint template, out IStream stream);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
