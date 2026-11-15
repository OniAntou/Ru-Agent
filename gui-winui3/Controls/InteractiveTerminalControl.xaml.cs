using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The Terminal tab's real terminal: xterm.js in a WebView2 rendering a
    /// ConPTY-backed shell for the user. The page and its scripts ship inside the
    /// app (no CDN), input and output cross the bridge as base64, and the shell
    /// lives until the app closes or the workspace changes.
    ///
    /// This is the user's terminal, not a model tool: the Agent can neither see
    /// nor call it, and the Broker stays the only execution authority for
    /// model-visible tool calls.
    /// </summary>
    public sealed partial class InteractiveTerminalControl : UserControl
    {
        private const string TerminalHostName = "ru-terminal.local";
        private const string TerminalOrigin = "https://ru-terminal.local/";
        private const string TerminalPageUrl = TerminalOrigin + "terminal.html";
        private const int MaxLinkChars = 2048;
        private const int MaxTerminalDimension = 1000;
        private const string ShellCommandLine = "powershell.exe -NoLogo";
        private const short DefaultColumns = 80;
        private const short DefaultRows = 24;

        private ConPtyTerminalSession? _session;
        private string? _workingDirectory;
        private bool _isWebInitialized;
        private bool _isWebReady;
        private bool _isStarting;
        private bool _shutdown;
        private bool _focusRequested;
        private readonly UiRequestLifetime _callbacks = new();
        private Action<byte[]>? _outputHandler;
        private Action? _exitHandler;

        public InteractiveTerminalControl()
        {
            InitializeComponent();
            TerminalWebView.SizeChanged += (_, _) => PostFit();
            ActualThemeChanged += (_, _) => PostTheme();
        }

        /// <summary>
        /// Shows the terminal for this working directory, starting the shell on
        /// first use. A different directory restarts the shell.
        /// </summary>
        public void EnsureStarted(string? workingDirectory, bool focus = true)
        {
            _shutdown = false;
            _focusRequested |= focus;
            string directory = ResolveWorkingDirectory(workingDirectory);
            bool directoryChanged = _workingDirectory is not null
                && !string.Equals(_workingDirectory, directory, StringComparison.OrdinalIgnoreCase);
            _workingDirectory = directory;

            if (directoryChanged)
            {
                StopSession();
                if (_isWebReady) PostMessage(new { type = "reset" });
            }

            if (_session is not null && _isWebReady)
            {
                ApplyRequestedFocus();
                return;
            }

            _ = EnsureWebAndSessionAsync();
        }

        /// <summary>Ends the shell; called when the app closes.</summary>
        public void Shutdown()
        {
            _shutdown = true;
            StopSession();
        }

        private async Task EnsureWebAndSessionAsync()
        {
            if (_isStarting)
            {
                return;
            }

            _isStarting = true;
            try
            {
                if (!_isWebInitialized)
                {
                    await TerminalWebView.EnsureCoreWebView2Async();
                    var core = TerminalWebView.CoreWebView2;
                    string assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
                    core.SetVirtualHostNameToFolderMapping(
                        TerminalHostName,
                        assetsPath,
                        CoreWebView2HostResourceAccessKind.DenyCors);
#if !DEBUG
                    core.Settings.AreDevToolsEnabled = false;
#endif
                    // Shell output is untrusted: the web view stays on the vendored page
                    // and never opens a window of its own.
                    core.NavigationStarting += OnNavigationStarting;
                    core.NewWindowRequested += OnNewWindowRequested;
                    core.WebMessageReceived += OnWebMessageReceived;
                    core.Navigate(TerminalPageUrl);
                    _isWebInitialized = true;
                }

                if (_isWebReady)
                {
                    StartSession();
                    ApplyRequestedFocus();
                }
            }
            catch (Exception)
            {
                ShowTerminalError("The terminal needs the Microsoft Edge WebView2 runtime (included with Windows 11).");
            }
            finally
            {
                _isStarting = false;
            }
        }

        private static bool IsTerminalPage(string? uri) =>
            uri is not null && uri.StartsWith(TerminalOrigin, StringComparison.OrdinalIgnoreCase);

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!IsTerminalPage(args.Uri))
            {
                args.Cancel = true;
            }
        }

        private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
            args.Handled = true;

        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!IsTerminalPage(args.Source))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProperty))
                {
                    return;
                }

                switch (typeProperty.GetString())
                {
                    case "ready":
                        _isWebReady = true;
                        PostTheme();
                        StartSession();
                        PostFit();
                        ApplyRequestedFocus();
                        break;
                    case "input" when root.TryGetProperty("data", out var input):
                        if (input.GetString() is { Length: > 0 } encoded)
                        {
                            _session?.Write(Convert.FromBase64String(encoded));
                        }

                        break;
                    case "resize" when root.TryGetProperty("cols", out var columns) && root.TryGetProperty("rows", out var rows):
                        _session?.Resize(ClampDimension(columns.GetInt32()), ClampDimension(rows.GetInt32()));
                        break;
                    case "link" when root.TryGetProperty("uri", out var link):
                        if (TryGetExternalLink(link.GetString(), out var uri))
                        {
                            _ = OpenExternalLinkAsync(uri);
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                // A malformed bridge message is dropped; the terminal stays usable.
            }
        }

        private static short ClampDimension(int value) => (short)Math.Clamp(value, 1, MaxTerminalDimension);

        /// <summary>Only an absolute http(s) address of bounded length leaves the terminal.</summary>
        internal static bool TryGetExternalLink(string? value, out Uri uri)
        {
            uri = null!;
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLinkChars
                || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
                || parsed.UserInfo.Length != 0)
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        private static async Task OpenExternalLinkAsync(Uri uri)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[Terminal] Opening a link failed: {ex.GetType().Name}");
            }
        }

        private void StartSession()
        {
            if (_shutdown || _session is not null || _workingDirectory is null)
            {
                return;
            }

            try
            {
                _session = ConPtyTerminalSession.Start(_workingDirectory, ShellCommandLine, DefaultColumns, DefaultRows);
                var version = _callbacks.Replace();
                _outputHandler = chunk => EnqueueOwned(version, () => PostData(chunk));
                _exitHandler = () => EnqueueOwned(version, () =>
                {
                    PostData(Encoding.UTF8.GetBytes("\r\n[shell exited]\r\n"));
                    StopSession();
                });
                _session.OutputReceived += _outputHandler;
                _session.Exited += _exitHandler;
            }
            catch (Exception)
            {
                ShowTerminalError("The shell could not be started.");
            }
        }

        private void EnqueueOwned(long version, Action apply)
        {
            var owned = _callbacks.Bind(version, apply);
            DispatcherQueue.TryEnqueue(() => owned());
        }

        private void StopSession()
        {
            _callbacks.Replace();
            var session = _session;
            _session = null;
            if (session is null)
            {
                return;
            }

            session.OutputReceived -= _outputHandler;
            session.Exited -= _exitHandler;
            _outputHandler = null;
            _exitHandler = null;
            session.Dispose();
        }

        private void PostData(byte[] chunk)
        {
            if (_isWebReady)
            {
                PostMessage(new { type = "data", data = Convert.ToBase64String(chunk) });
            }
        }

        private void PostFit()
        {
            if (_isWebReady)
            {
                PostMessage(new { type = "fit" });
            }
        }

        private void PostFocus()
        {
            if (_isWebReady)
            {
                PostMessage(new { type = "focus" });
            }
        }

        private void PostTheme()
        {
            if (!_isWebReady) return;
            string Color(string key)
            {
                var color = ((SolidColorBrush)Application.Current.Resources[key]).Color;
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            PostMessage(new
            {
                type = "theme",
                background = Color("RuCanvasBrush"),
                foreground = Color("RuInkBrush"),
                cursor = Color("RuInkBrush"),
                selectionBackground = Color("RuLineBrush"),
                scrollbar = Color("RuLineBrush"),
                light = ActualTheme == ElementTheme.Light,
            });
        }

        private void ApplyRequestedFocus()
        {
            if (!_focusRequested) return;
            _focusRequested = false;
            PostFocus();
        }

        private void PostMessage(object payload)
        {
            try
            {
                TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The web view is tearing down; there is nothing left to post to.
            }
        }

        private void ShowTerminalError(string message)
        {
            TerminalErrorText.Text = message;
            TerminalErrorText.Visibility = Visibility.Visible;
        }

        private static string ResolveWorkingDirectory(string? workingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                return workingDirectory;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
