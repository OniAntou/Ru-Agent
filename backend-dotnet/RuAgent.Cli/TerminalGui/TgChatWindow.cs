using System.Drawing;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The three-region window: pinned header, scrolling transcript, pinned composer stack and
/// hint row. State changes go through this facade so the run loop never touches views.
/// Slash suggestions and approval/trust/picker/secret panels float over the transcript.
/// </summary>
public sealed class TgChatWindow : Window
{
    private TgSlashDropdown? _slashDropdown;
    private TgPanelHost? _panel;
    private ApprovalPrompt? _approval;
    private ChoiceList? _choices;
    private bool _secretPanel;
    private IApplication? _resizeApp;

    private TgChatWindow()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));

        Header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        Transcript = new TgTranscriptView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8) };
        // The classic footer is seven rows: six for the composer stack (top border at L-7,
        // bottom border at L-2) and one for the hint row (L-1).
        Composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 6 };
        Hints = new TgHintsView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(Header, Transcript, Composer, Hints);
        CanFocus = true;

        // The dropdown is driven from the composer text itself: every edit (typed, history,
        // programmatic) refreshes it, and accepting writes the command back through the same
        // channel so the two can never disagree.
        Composer.Input.TextChanged += (_, _) => RefreshSlashDropdown();
    }

    public TgHeaderView Header { get; }
    public TgTranscriptView Transcript { get; }
    public TgComposerView Composer { get; }
    public TgHintsView Hints { get; }

    public bool IsSlashDropdownOpen => _slashDropdown?.SuperView is not null;

    /// <summary>
    /// Set once the live terminal shrinks below the 80x24 floor: the window stops the app
    /// and the run loop continues the same session on the classic sequential path. One-way
    /// for the life of the window; the TG frame never reopens.
    /// </summary>
    public bool HandOverToSequential { get; private set; }

    /// <summary>Decision token of a confirmed approval/trust/picker panel.</summary>
    public event Action<string>? PanelConfirmed;

    /// <summary>Escape on a panel: an approval denies, a picker aborts.</summary>
    public event Action? PanelCancelled;

    /// <summary>
    /// Focus must be granted after the app's first loop iterations; a SetFocus before
    /// Run (or on the first tick) is ignored, which the spike reproduced. Callers use
    /// this from a one-shot timeout once the window is live.
    /// </summary>
    public void FocusComposer()
    {
        SetFocus();
        Composer.Input.SetFocus();
    }

    /// <summary>
    /// Opens or refreshes the slash dropdown for the composer's current text. The composer
    /// keeps focus — typing must keep filtering the list — so the run loop routes Up/Down,
    /// Enter/Tab and Escape here instead of the composer.
    /// </summary>
    public void ShowSlashDropdown()
    {
        var matches = InteractiveLineReader.FindMatches(Composer.Input.Text?.ToString() ?? string.Empty);
        if (matches.Count == 0)
        {
            HideSlashDropdown();
            return;
        }

        if (_slashDropdown is null)
        {
            _slashDropdown = new TgSlashDropdown { X = 0, Width = Dim.Fill() };
            _slashDropdown.CommandAccepted += command =>
            {
                Composer.Input.Text = command + " ";
                HideSlashDropdown();
                Composer.Input.SetFocus();
            };
            _slashDropdown.Closed += HideSlashDropdown;
        }

        _slashDropdown.SetMatches(matches);
        _slashDropdown.Height = _slashDropdown.PanelHeight;
        _slashDropdown.Y = Pos.AnchorEnd(7 + _slashDropdown.PanelHeight);
        if (_slashDropdown.SuperView is null)
        {
            Add(_slashDropdown);
        }

        SetNeedsDraw();
    }

    public void HideSlashDropdown()
    {
        if (_slashDropdown?.SuperView is not null)
        {
            Remove(_slashDropdown);
        }

        SetNeedsDraw();
    }

    public void MoveSlashDropdown(int delta) => _slashDropdown?.Move(delta);

    public void AcceptSlashDropdown() => _slashDropdown?.Accept();

    /// <summary>
    /// The approval panel for an already-built prompt: the caller's choices (including a
    /// folder or always grant) and full summary are rendered as-is, never rebuilt from a
    /// truncated copy.
    /// </summary>
    public void ShowApproval(ApprovalPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ShowApprovalPanel(prompt);
    }

    /// <summary>The approval panel for a safe wire summary (choices via <see cref="ApprovalPrompt.For"/>).</summary>
    public void ShowApproval(string safeSummary, bool offerDirectory = false) =>
        ShowApproval(ApprovalPrompt.For(safeSummary, offerDirectory));

    /// <summary>The workspace-trust panel for an already-built prompt; Enter defaults to cancel.</summary>
    public void ShowWorkspaceTrust(ApprovalPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ShowApprovalPanel(prompt);
    }

    /// <summary>The workspace-trust panel; Enter defaults to cancel like the classic prompt.</summary>
    public void ShowWorkspaceTrust(string workspacePath) =>
        ShowWorkspaceTrust(ApprovalPrompt.ForWorkspaceTrust(workspacePath));

    private void ShowApprovalPanel(ApprovalPrompt prompt)
    {
        _approval = prompt;
        _choices = null;
        _secretPanel = false;
        ShowPanel(width => CenterPanel(prompt.Render(TuiFloatingPanel.ComputeWidth(width)), width));
    }

    /// <summary>A picker over the transcript, navigated by the shared <see cref="ChoiceList"/> grammar.</summary>
    public void ShowPicker(string title, IReadOnlyList<ChoiceItem> items, int selected = 0, string? footer = null)
    {
        var list = new ChoiceList(title, items, selected, footer);
        _approval = null;
        _choices = list;
        _secretPanel = false;
        ShowPanel(width => CenterPanel(list.Render(TuiFloatingPanel.ComputeWidth(width)), width));
    }

    /// <summary>The masked secret panel; only the bullet count reaches the screen.</summary>
    public void ShowSecret(string providerDisplay, int bulletCount = 0)
    {
        _approval = null;
        _choices = null;
        _secretPanel = true;
        ShowPanel(width => CenterPanel(
            KeyInputPanel.Compose(TuiFloatingPanel.ComputeWidth(width), providerDisplay, bulletCount),
            width));
    }

    public void HidePanel()
    {
        if (_panel?.SuperView is not null)
        {
            Remove(_panel);
        }

        _approval = null;
        _choices = null;
        _secretPanel = false;
        if (App is not null)
        {
            FocusComposer();
        }

        SetNeedsDraw();
    }

    public static TgChatWindow Build() => new();

    /// <summary>
    /// Watches the app's resize feed for as long as the window is running: a size change
    /// relayouts the pinned regions, and a drop below the TG floor (the same 80x24 the
    /// engine selection gates on) ends the frame in favour of the sequential fallback.
    /// </summary>
    protected override void OnIsRunningChanged(bool args)
    {
        base.OnIsRunningChanged(args);
        if (args)
        {
            _resizeApp = App;
            if (_resizeApp is not null)
            {
                _resizeApp.ScreenChanged += OnScreenChanged;
            }
        }
        else if (_resizeApp is not null)
        {
            _resizeApp.ScreenChanged -= OnScreenChanged;
            _resizeApp = null;
        }
    }

    private void OnScreenChanged(object? sender, EventArgs<Rectangle> args)
    {
        if (args.Value.Width < TuiViewport.MinAltBufferWidth
            || args.Value.Height < TuiViewport.MinAltBufferHeight)
        {
            HandOverToSequential = true;
            _resizeApp?.RequestStop();
            return;
        }

        Layout();
        SetNeedsDraw();
    }

    private void ShowPanel(Func<int, IReadOnlyList<string>> render)
    {
        // A modal panel takes the keyboard; the suggestion list must not linger behind it.
        HideSlashDropdown();
        if (_panel is null)
        {
            _panel = new TgPanelHost { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8) };
            _panel.PanelKey += OnPanelKey;
        }

        _panel.SetRenderer(render);
        if (_panel.SuperView is null)
        {
            Add(_panel);
        }

        SetNeedsDraw();
        _panel.SetFocus();
    }

    private static IReadOnlyList<string> CenterPanel(IReadOnlyList<string> lines, int width) =>
        TuiFloatingPanel.Center(lines, width);

    private void RefreshSlashDropdown()
    {
        var text = Composer.Input.Text?.ToString() ?? string.Empty;
        // Mirrors the classic isSlash gate: a space means the command is being parameterised,
        // which also closes the list right after an accepted command gains its trailing space.
        if (text.StartsWith('/') && !text.Contains(' '))
        {
            ShowSlashDropdown();
        }
        else
        {
            HideSlashDropdown();
        }
    }

    private void OnPanelKey(TgPanelKey key)
    {
        if (_approval is { } approval)
        {
            switch (key.Kind)
            {
                case TgPanelKeyKind.Up:
                    approval.Move(-1);
                    break;
                case TgPanelKeyKind.Down:
                    approval.Move(1);
                    break;
                case TgPanelKeyKind.Enter:
                    ResolveConfirmed(approval.Current.Decision);
                    return;
                case TgPanelKeyKind.Escape:
                    ResolveCancelled();
                    return;
                case TgPanelKeyKind.Digit:
                    if (approval.TrySelectDigit(key.Digit))
                    {
                        ResolveConfirmed(approval.Current.Decision);
                    }

                    return;
            }

            _panel?.SetNeedsDraw();
            return;
        }

        if (_choices is { } choices)
        {
            switch (key.Kind)
            {
                case TgPanelKeyKind.Up:
                    choices.Move(-1);
                    break;
                case TgPanelKeyKind.Down:
                    choices.Move(1);
                    break;
                case TgPanelKeyKind.Enter:
                    ResolveConfirmed(choices.Current.Value);
                    return;
                case TgPanelKeyKind.Escape:
                    ResolveCancelled();
                    return;
                case TgPanelKeyKind.Digit:
                    if (choices.TrySelectDigit(key.Digit))
                    {
                        ResolveConfirmed(choices.Current.Value);
                    }

                    return;
            }

            _panel?.SetNeedsDraw();
            return;
        }

        if (_secretPanel && key.Kind == TgPanelKeyKind.Escape)
        {
            ResolveCancelled();
        }
    }

    private void ResolveConfirmed(string value)
    {
        HidePanel();
        PanelConfirmed?.Invoke(value);
    }

    private void ResolveCancelled()
    {
        HidePanel();
        PanelCancelled?.Invoke();
    }
}
