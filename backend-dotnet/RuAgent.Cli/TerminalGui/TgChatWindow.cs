using System.Drawing;
using System.Text;
using RuAgent.Cli.Images;
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
    /// <summary>Masked secret input is bounded like the sequential reader's secret prompt.</summary>
    private const int MaxSecretChars = 4096;

    private TgSlashDropdown? _slashDropdown;
    private readonly TgCommandPalette _commandPalette = new();
    private TgCommandPaletteView? _commandPaletteView;
    private string? _commandPaletteDraft;
    private TgPanelHost? _panel;
    private TgDocumentPanel? _document;
    private ApprovalPrompt? _approval;
    private ChoiceList? _choices;
    private bool _secretPanel;
    private readonly StringBuilder _secret = new();
    private string _secretProvider = string.Empty;
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
        Landing = new TgLandingView();

        Add(Header, Transcript, Landing, Composer, Hints);
        CanFocus = true;

        // The pre-chat wordmark owns the transcript region only while it is empty; the
        // first row (or /clear) toggles it exactly like the classic landing screen.
        Transcript.FirstContentAppended += HideLanding;
        Transcript.ContentCleared += ShowLanding;
        Transcript.UnseenOutputChanged += hasUnseenOutput => Hints.SetTranscriptUnread(hasUnseenOutput);
        Transcript.FollowingTailChanged += followingTail => Hints.SetTranscriptFollowing(followingTail);

        // The dropdown is driven from the composer text itself: every edit (typed, history,
        // programmatic) refreshes it, and accepting writes the command back through the same
        // channel so the two can never disagree.
        Composer.Input.TextChanged += (_, _) => RefreshSlashDropdown();
    }

    public TgHeaderView Header { get; }
    public TgTranscriptView Transcript { get; }
    public TgComposerView Composer { get; }
    public TgHintsView Hints { get; }

    /// <summary>The aurora/gray wordmark shown until the transcript has content.</summary>
    public TgLandingView Landing { get; }

    private void HideLanding()
    {
        Landing.Visible = false;
        Landing.SetNeedsDraw();
    }

    private void ShowLanding()
    {
        Landing.Visible = true;
        Landing.SetNeedsDraw();
    }

    public bool IsSlashDropdownOpen => _slashDropdown?.SuperView is not null;
    public bool IsCommandPaletteOpen => _commandPaletteView?.SuperView is not null;
    public bool IsModalPanelOpen => _panel?.SuperView is not null;
    public string CommandPaletteQuery => _commandPalette.Query;

    /// <summary>True while the read-only output window is over the transcript.</summary>
    public bool IsDocumentPanelOpen => _document?.SuperView is not null;

    /// <summary>Test seam: the open output window.</summary>
    public TgDocumentPanel Document => _document ?? throw new InvalidOperationException("no document window");

    /// <summary>
    /// Opens the read-only output window over the transcript for the commands that only
    /// report. The rows stay in the transcript too: closing the window is a look, not a
    /// discard, so the operator can still scroll back to the report afterwards.
    /// </summary>
    public void ShowDocument(string title, IReadOnlyList<string> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        // The window takes the keyboard, so the suggestion list must not linger behind it.
        HideSlashDropdown();
        HidePanel();
        if (_document is null)
        {
            _document = new TgDocumentPanel { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8) };
            _document.Closed += HideDocument;
        }

        _document.SetContent(title, body);
        if (_document.SuperView is null)
        {
            Add(_document);
        }

        SetNeedsDraw();
        _document.SetFocus();
    }

    public void HideDocument()
    {
        if (_document?.SuperView is not null)
        {
            Remove(_document);
            DocumentClosed?.Invoke();
        }

        SetNeedsDraw();
        if (App is not null)
        {
            FocusComposer();
        }
    }

    /// <summary>
    /// Set once the live terminal has settled below the drawable 20x12 floor: the window stops the app
    /// and the run loop continues the same session on the append-only text path. One-way
    /// for the life of the window; the TG frame never reopens.
    /// </summary>
    public bool HandOverToSequential { get; private set; }

    /// <summary>
    /// How long the terminal must stay below the floor before the frame gives up. Dragging a
    /// window edge sweeps the width through small values for a moment, and the handover cannot
    /// be undone, so reacting to the first blip costs the operator the whole Terminal.Gui
    /// surface over a stray pixel of drag.
    /// </summary>
    private const int HandoverDebounceMs = 400;

    /// <summary>
    /// The size below which the layout genuinely cannot be drawn, which is a very different
    /// question from the 80x24 floor that picks the engine at startup. That floor asks "which
    /// surface suits this screen"; this one asks "can this surface still be laid out", and the
    /// honest answer stays yes far below it. Width barely matters: the wordmark falls back to
    /// a caption under <see cref="TuiHeader.MinWordmarkWidth"/> and the composer clamps at ten
    /// columns. Height is the real constraint, because the header, transcript and the pinned
    /// composer stack are what stop fitting.
    /// </summary>
    private const int HandoverMinWidth = 20;

    /// <summary>Header, transcript, the six composer rows and the hint row cannot share fewer.</summary>
    private const int HandoverMinHeight = 12;

    private bool _handoverScheduled;

    /// <summary>Decision token of a confirmed approval/trust/picker panel.</summary>
    public event Action<string>? PanelConfirmed;

    /// <summary>Escape on a panel: an approval denies, a picker aborts.</summary>
    public event Action? PanelCancelled;

    /// <summary>The read-only output window closed, by Enter or by Escape.</summary>
    public event Action? DocumentClosed;

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
        var matches = ChatCommandCatalog.FindMatches(Composer.Input.Text?.ToString() ?? string.Empty);
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

    public string? AcceptSlashDropdown() => _slashDropdown?.Accept();

    public void OpenCommandPalette()
    {
        if (IsModalPanelOpen || IsCommandPaletteOpen)
        {
            return;
        }

        HideSlashDropdown();
        _commandPaletteDraft = Composer.Input.Text?.ToString() ?? string.Empty;
        _commandPalette.Reset();
        _commandPaletteView ??= new TgCommandPaletteView(_commandPalette);
        _commandPaletteView.X = 0;
        _commandPaletteView.Width = Dim.Fill();
        var paletteHeight = Math.Min(
            TgCommandPalette.MaxVisibleCommands + 2,
            Math.Max(3, Transcript.Viewport.Height));
        _commandPaletteView.Height = paletteHeight;
        _commandPaletteView.Y = Pos.AnchorEnd(7 + paletteHeight);
        Add(_commandPaletteView);
        Composer.Input.SetFocus();
        SetNeedsDraw();
    }

    public void SetCommandPaletteQuery(string query)
    {
        if (!IsCommandPaletteOpen)
        {
            return;
        }

        _commandPalette.SetQuery(query);
        _commandPaletteView?.SetNeedsDraw();
    }

    public void MoveCommandPaletteSelection(int delta)
    {
        if (!IsCommandPaletteOpen)
        {
            return;
        }

        _commandPalette.Move(delta);
        _commandPaletteView?.SetNeedsDraw();
    }

    public string? SelectedCommandPaletteCommand => IsCommandPaletteOpen ? _commandPalette.SelectedCommand : null;

    public void AcceptCommandPaletteSelection()
    {
        if (SelectedCommandPaletteCommand is not { } command)
        {
            return;
        }

        Composer.Input.Text = command + " ";
        CloseCommandPalette(restoreDraft: false);
    }

    public void DismissCommandPalette() => CloseCommandPalette(restoreDraft: true);

    public void ShowCommandPaletteBusyHint()
    {
        _commandPalette.SetStatusMessage("active turn in progress · apply command after it finishes");
        _commandPaletteView?.SetNeedsDraw();
        Hints.SetBusy("⠋", "finish turn before applying command");
    }

    private void CloseCommandPalette(bool restoreDraft)
    {
        if (_commandPaletteView?.SuperView is not null)
        {
            Remove(_commandPaletteView);
        }

        if (restoreDraft && _commandPaletteDraft is not null)
        {
            Composer.Input.Text = _commandPaletteDraft;
        }

        _commandPaletteDraft = null;
        Composer.Input.SetFocus();
        SetNeedsDraw();
    }

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
        ShowPanel(width => CenterPanel(prompt.Render(TuiFloatingPanel.ComputeWidth(width)), width), null);
    }

    /// <summary>A picker over the transcript, navigated by the shared <see cref="ChoiceList"/> grammar.</summary>
    public void ShowPicker(string title, IReadOnlyList<ChoiceItem> items, int selected = 0, string? footer = null)
    {
        var list = new ChoiceList(title, items, selected, footer, showRowCount: true);
        _approval = null;
        _choices = list;
        _secretPanel = false;
        ShowPanel(null, width => TuiCard.ComposeList(
            TuiCard.ComputeWidth(width),
            title,
            "esc",
            CardItems(list),
            list.Selected,
            PickerVisibleRows,
            PickerFooter(list),
            list.Filter.Length == 0 ? SearchHint : $"{SearchHint} {list.Filter}"));
    }

    private const string SearchHint = "Search";

    /// <summary>Rows a picker spends on entries; the card frame takes the rest.</summary>
    private const int PickerVisibleRows = 8;

    private static IReadOnlyList<CardItem> CardItems(ChoiceList list) =>
        list.Items
            .Select(item => new CardItem(item.Label, string.IsNullOrEmpty(item.Detail) ? null : item.Detail, item.Enabled))
            .ToList();

    private static string PickerFooter(ChoiceList list) =>
        string.IsNullOrWhiteSpace(list.Footer)
            ? "↑↓ select · enter apply"
            : $"{list.Footer} · {list.TotalCount} models";

    /// <summary>
    /// The masked secret panel: typed characters append to the buffer, Backspace trims it,
    /// Enter confirms the secret and Escape cancels. Only the bullet count ever reaches the
    /// screen; the buffer is cleared when the panel closes.
    /// </summary>
    public void ShowSecret(string providerDisplay)
    {
        _approval = null;
        _choices = null;
        _secretPanel = true;
        _secret.Clear();
        _secretProvider = string.IsNullOrWhiteSpace(providerDisplay) ? "api key" : providerDisplay.Trim();
        ShowPanel(width => CenterPanel(
            KeyInputPanel.Compose(TuiFloatingPanel.ComputeWidth(width), _secretProvider, _secret.Length),
            width), null);
    }

    /// <summary>
    /// <c>/clear</c> in TG mode: drop every transcript row and reset the header's usage
    /// cluster, in place, without touching the pinned regions or the composer.
    /// </summary>
    public void ResetTranscript()
    {
        Transcript.Clear();
        Header.UpdateUsage(0, null, null);
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
        _secret.Clear();
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
        if (IsBelowFloor(args.Value))
        {
            // Not a handover yet: keep the frame and relayout so it stays usable while the
            // terminal decides whether it really is too small.
            ScheduleHandover();
            Relayout();
            return;
        }

        Relayout();
    }

    /// <summary>
    /// Lays out synchronously rather than only asking for a layout pass. Scheduling one leaves
    /// a window in which the driver paints before the relayout runs, and every view that centres
    /// itself from its viewport paints that frame with the old width: the landing wordmark lands
    /// off to the right and runs off the edge. Laying out here costs a pass on a resize and
    /// removes the stale frame entirely.
    /// </summary>
    private void Relayout()
    {
        Layout();
        SetNeedsDraw();
    }

    private static bool IsBelowFloor(Rectangle size) => IsTooSmallToDraw(size.Width, size.Height);

    /// <summary>
    /// Whether a terminal of this size cannot host the layout at all. Separate from the 80x24
    /// floor that picks the engine at startup on purpose, and separate from any driver, so the
    /// rule can be pinned directly instead of through a screen the test driver may refuse.
    /// </summary>
    internal static bool IsTooSmallToDraw(int width, int height) =>
        width < HandoverMinWidth || height < HandoverMinHeight;

    /// <summary>
    /// Arms the one-way handover, and disarms it again if the terminal is back above the floor
    /// by the time it fires. A settled-small terminal still loses the frame; a drag that only
    /// passes through the floor for a moment does not.
    /// </summary>
    private void ScheduleHandover()
    {
        if (_handoverScheduled)
        {
            return;
        }

        _handoverScheduled = true;
        _resizeApp?.AddTimeout(TimeSpan.FromMilliseconds(HandoverDebounceMs), () =>
        {
            _handoverScheduled = false;
            var driver = _resizeApp?.Driver;
            if (driver is not null && !IsBelowFloor(new Rectangle(0, 0, driver.Cols, driver.Rows)))
            {
                return false;
            }

            HandOverToSequential = true;
            _resizeApp?.RequestStop();
            return false;
        });
    }

    private void ShowPanel(Func<int, IReadOnlyList<string>>? render, Func<int, IReadOnlyList<CardLine>>? card)
    {
        // A modal panel takes the keyboard; the suggestion list must not linger behind it.
        HideSlashDropdown();
        if (_panel is null)
        {
            _panel = new TgPanelHost { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8) };
            _panel.PanelKey += OnPanelKey;
        }

        if (card is not null)
        {
            _panel.SetCard(card);
        }
        else
        {
            _panel.SetRenderer(render ?? (_ => []));
        }

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
                case TgPanelKeyKind.Backspace:
                    choices.TryTrimFilter();
                    _panel?.SetNeedsDraw();
                    return;
                case TgPanelKeyKind.Character:
                    // The card's search field: typing narrows the list, Backspace widens it.
                    // Without this the field would be a label over a list nothing filters.
                    choices.TryAppendFilter(key.Character);
                    _panel?.SetNeedsDraw();
                    return;
            }

            _panel?.SetNeedsDraw();
            return;
        }

        if (_secretPanel)
        {
            switch (key.Kind)
            {
                case TgPanelKeyKind.Escape:
                    ResolveCancelled();
                    return;
                case TgPanelKeyKind.Enter:
                    ResolveConfirmed(_secret.ToString());
                    return;
                case TgPanelKeyKind.Backspace:
                    if (_secret.Length > 0)
                    {
                        _secret.Length--;
                    }

                    _panel?.SetNeedsDraw();
                    return;
                case TgPanelKeyKind.Paste:
                    AppendSecret(CliClipboard.TryReadText());
                    return;
                case TgPanelKeyKind.Character:
                    AppendSecret(key.Character.ToString());
                    return;
                case TgPanelKeyKind.Digit:
                    // The panel host keeps digits as the numbered-pick shortcut; inside a
                    // secret they are ordinary key characters.
                    AppendSecret(key.Digit.ToString());
                    return;
            }
        }
    }

    /// <summary>
    /// Appends sanitized input to the masked buffer, bounded like the classic reader.
    /// Clipboard text keeps its first line only, so a copied key never carries a newline.
    /// </summary>
    private void AppendSecret(string? text)
    {
        var sanitized = ChatCommandCatalog.SanitizePastedText(
            text,
            MaxSecretChars - _secret.Length);
        if (sanitized.Length > 0)
        {
            _secret.Append(sanitized);
        }

        _panel?.SetNeedsDraw();
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
