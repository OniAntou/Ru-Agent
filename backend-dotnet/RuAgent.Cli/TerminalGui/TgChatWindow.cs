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
/// Responsive landing/session window with a scrolling transcript, composer and session header.
/// State changes go through this facade so the run loop never touches views.
/// Slash suggestions and approval/trust/picker/secret panels float over the transcript.
/// </summary>
public sealed partial class TgChatWindow : Window
{
    private sealed record ModalInputState(string Text, int InsertionPoint);

    /// <summary>Masked secret input is bounded like the sequential reader's secret prompt.</summary>
    private const int MaxSecretChars = 4096;

    private TgSlashDropdown? _slashDropdown;
    private readonly TgCommandPalette _commandPalette = new();
    private TgCommandPaletteView? _commandPaletteView;
    private string? _commandPaletteDraft;
    private int _commandPaletteDraftCursor;
    private TgPanelHost? _panel;
    private TgDocumentPanel? _document;
    private ApprovalPrompt? _approval;
    private ChoiceList? _choices;
    private bool _choicesAllowDelete;
    private bool _approvalWaiting;
    private bool _secretPanel;
    private readonly StringBuilder _secret = new();
    private string _secretProvider = string.Empty;
    private IApplication? _resizeApp;
    private ModalInputState? _modalInputState;
    private bool _relayoutInProgress;
    internal bool IsSessionPage { get; set; }
    internal void RefreshSessionLayout() => Relayout();

    private TgChatWindow()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));

        Header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        HeaderDivider = new TgRuleView();
        Transcript = new TgTranscriptView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(TgComposerView.RowCount + 2) };
        Composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(TgComposerView.RowCount + 1), Width = Dim.Fill(), Height = TgComposerView.RowCount };
        Composer.PresetChanged += preset => { Header.SetPreset(preset); Relayout(); };
        Hints = new TgHintsView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        Landing = new TgLandingView
        {
            IsWordmarkOccluded = frame => IsSlashDropdownOpen && _slashDropdown!.Frame.IntersectsWith(frame),
        };

        Add(Transcript, Landing, Header, HeaderDivider, Composer, Hints);
        CanFocus = true;

        // The pre-chat wordmark owns the transcript region only while it is empty; the
        // first row (or /clear) toggles it exactly like the classic landing screen.
        Transcript.FirstContentAppended += HideLanding;
        Transcript.ContentCleared += ShowLanding;
        Transcript.LiveActivityChanged += hasLive =>
        {
            // A turn's live row (thinking/tool/writing) is content the operator asked for:
            // the wordmark must not sit on top of it while the first rows are still landing.
            if (hasLive)
            {
                HideLanding();
            }
            else if (!Transcript.HasContent)
            {
                // A turn that produced no rows (e.g. Continue on an empty transcript)
                // returns to the wordmark when its live row clears.
                ShowLanding();
            }
        };
        Transcript.UnseenOutputChanged += hasUnseenOutput => Hints.SetTranscriptUnread(hasUnseenOutput);
        Transcript.FollowingTailChanged += followingTail => Hints.SetTranscriptFollowing(followingTail);

        // The dropdown is driven from the composer text itself: every edit (typed, history,
        // programmatic) refreshes it, and accepting writes the command back through the same
        // channel so the two can never disagree.
        Composer.Input.ContentsChanged += (_, _) => RefreshSlashDropdown();
        Composer.LayoutChanged += (_, _) => Relayout();
    }

    public TgHeaderView Header { get; }
    public TgRuleView HeaderDivider { get; }
    public TgTranscriptView Transcript { get; }
    public TgComposerView Composer { get; }
    public TgHintsView Hints { get; }

    /// <summary>The aurora/gray wordmark shown until the transcript has content.</summary>
    public TgLandingView Landing { get; }

    internal void TickWelcome()
    {
        Landing.AnimationPaused = IsModalPanelOpen || IsDocumentPanelOpen || IsCommandPaletteOpen;
        Landing.Tick();
    }

    private void HideLanding()
    {
        Landing.ClearRasterWordmark();
        Landing.Visible = false;
        Relayout();
        Landing.SetNeedsDraw();
    }

    private void ShowLanding()
    {
        Landing.Visible = true;
        Relayout();
        Landing.SetNeedsDraw();
    }

    public bool IsSlashDropdownOpen => _slashDropdown?.SuperView is not null;
    public bool IsCommandPaletteOpen => _commandPaletteView?.SuperView is not null;
    public bool IsModalPanelOpen => _panel?.SuperView is not null || _questionPanel?.SuperView is not null;
    internal SemaphoreSlim ModalRequestGate { get; } = new(1, 1);
    public string CommandPaletteQuery => _commandPalette.Query;

    /// <summary>Routes terminal text to the active input without changing a hidden draft.</summary>
    internal bool TryPasteText(string text)
    {
        if (_questionPanel?.SuperView is not null)
        {
            _questionPanel.Paste(text);
            return true;
        }
        if (IsCommandPaletteOpen)
        {
            var firstLine = text.Split(['\r', '\n'], 2)[0];
            SetCommandPaletteQuery(CommandPaletteQuery + firstLine);
            return true;
        }

        if (_secretPanel && IsModalPanelOpen)
        {
            AppendSecret(text);
            return true;
        }

        if (IsModalPanelOpen || IsDocumentPanelOpen || MostFocused != Composer.Input)
        {
            return false;
        }

        Composer.Input.TryInsertDraft(text);
        return true; // Refused oversized input is handled too; never bubble into another view.
    }

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
            _document = new TgDocumentPanel();
            _document.Closed += HideDocument;
            TgModalLayout.Apply(_document, this, measureHeight: _document.MeasureHeight);
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

    /// <summary>Decision token of a confirmed approval/trust/picker panel.</summary>
    public event Action<string>? PanelConfirmed;

    /// <summary>Escape on a panel: an approval denies, a picker aborts.</summary>
    public event Action? PanelCancelled;

    /// <summary>
    /// Delete on a picker that allows it: the value of the row under the cursor, so the owner
    /// can confirm and run the destructive action. The panel closes like a confirm.
    /// </summary>
    public event Action<string>? PanelDeleteRequested;

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
            _slashDropdown = new TgSlashDropdown
            {
                X = Pos.Left(Composer),
                Width = Dim.Width(Composer)
            };
            _slashDropdown.CommandAccepted += command =>
            {
                Composer.Input.Text = command + " ";
                Composer.Input.InsertionPoint = Composer.Input.Text.Length;
                HideSlashDropdown();
                Composer.Input.SetFocus();
            };
            _slashDropdown.Closed += HideSlashDropdown;
        }

        _slashDropdown.SetMatches(matches);
        int DropdownHeight() => Math.Min(_slashDropdown.PanelHeight, Math.Max(3, Composer.Frame.Y));
        _slashDropdown.Height = Dim.Func(_ => DropdownHeight());
        // Frame.Height still describes the previous match set while Y is measured.
        // Use the current measurement for both so filtering cannot leave a gap.
        _slashDropdown.Y = Pos.Func(_ => Math.Max(0, Composer.Frame.Y - DropdownHeight()));
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
        _commandPaletteDraftCursor = Composer.Input.InsertionPoint;
        _commandPalette.Reset();
        _commandPaletteView ??= new TgCommandPaletteView(_commandPalette);
        TgModalLayout.Apply(_commandPaletteView, this, TgCommandPalette.MaxVisibleCommands + 2);
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
        Composer.Input.InsertionPoint = Composer.Input.Text.Length;
        CloseCommandPalette(restoreDraft: false);
    }

    public void DismissCommandPalette() => CloseCommandPalette(restoreDraft: true);

    public void ShowCommandPaletteBusyHint()
    {
        _commandPalette.SetStatusMessage("active turn in progress · apply command after it finishes");
        _commandPaletteView?.SetNeedsDraw();
        Hints.ShowFeedback("finish turn before applying command", true);
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
            Composer.Input.InsertionPoint = _commandPaletteDraftCursor;
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
        SetApprovalWaiting(true);
        _approval = prompt;
        _choices = null;
        _secretPanel = false;
        ShowPanel((width, height) => TuiCard.ComposeFitting(
            TuiCard.ComputeWidth(width),
            height,
            prompt.Title,
            "esc",
            prompt.CardBody(),
            prompt.Footer));
    }

    /// <summary>A picker over the transcript, navigated by the shared <see cref="ChoiceList"/> grammar.</summary>
    public void ShowPicker(
        string title,
        IReadOnlyList<ChoiceItem> items,
        int selected = 0,
        string? footer = null,
        bool allowDelete = false)
    {
        SetApprovalWaiting(false);
        var safeTitle = ChatDisplayText.Sanitize(title, 120);
        var list = new ChoiceList(safeTitle, items, selected, footer);
        _approval = null;
        _choices = list;
        _choicesAllowDelete = allowDelete;
        _secretPanel = false;
        ShowPanel((width, height) => TuiCard.ComposeListFitting(
            TuiCard.ComputeWidth(width),
            height,
            safeTitle,
            "esc",
            CardItems(list),
            list.Selected,
            PickerFooter(list),
            list.Filter.Length == 0 ? SearchHint : $"{SearchHint} {list.Filter}"));
    }

    private const string SearchHint = "Search";

    private static IReadOnlyList<CardItem> CardItems(ChoiceList list) =>
        list.Items
            .Select(item => new CardItem(
                ChatDisplayText.Sanitize(item.Label, 160),
                string.IsNullOrEmpty(item.Detail) ? null : ChatDisplayText.Sanitize(item.Detail, 240),
                item.Enabled))
            .ToList();

    /// <summary>The picker footer: the caller's copy, or the shared navigation hints.</summary>
    private static string PickerFooter(ChoiceList list) =>
        string.IsNullOrWhiteSpace(list.Footer)
            ? "↑↓ select · enter apply"
            : ChatDisplayText.Sanitize(list.Footer, 160);

    /// <summary>
    /// The masked secret panel: typed characters append to the buffer, Backspace trims it,
    /// Enter confirms the secret and Escape cancels. Only the bullet count ever reaches the
    /// screen; the buffer is cleared when the panel closes.
    /// </summary>
    public void ShowSecret(string providerDisplay)
    {
        SetApprovalWaiting(false);
        _approval = null;
        _choices = null;
        _secretPanel = true;
        _secret.Clear();
        _secretProvider = string.IsNullOrWhiteSpace(providerDisplay) ? "api key" : providerDisplay.Trim();
        ShowPanel((width, height) => TuiCard.ComposeFitting(
            TuiCard.ComputeWidth(width),
            height,
            KeyInputPanel.CardTitle(_secretProvider),
            "esc",
            KeyInputPanel.CardBody(_secret.Length),
            "ctrl+v paste · enter save · esc cancel"));
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
        if (_questionPanel?.SuperView is not null)
        {
            Remove(_questionPanel);
            _questionPanel.Dispose();
            _questionPanel = null;
        }
        SetApprovalWaiting(false);
        var inputState = _modalInputState;
        _modalInputState = null;
        if (_panel?.SuperView is not null)
        {
            Remove(_panel);
        }

        _approval = null;
        _choices = null;
        _choicesAllowDelete = false;
        _secretPanel = false;
        _secret.Clear();
        if (App is not null)
        {
            if (inputState is not null)
            {
                Composer.Input.Text = inputState.Text;
                Composer.Input.InsertionPoint = Math.Clamp(
                    inputState.InsertionPoint,
                    0,
                    Composer.Input.Text?.Length ?? 0);
            }

            FocusComposer();
        }

        SetNeedsDraw();
    }

    private void SetApprovalWaiting(bool waiting)
    {
        if (_approvalWaiting == waiting)
        {
            return;
        }

        _approvalWaiting = waiting;
        Hints.SetWaitingForApproval(waiting);
    }

    public static TgChatWindow Build() => new();

    /// <summary>
    /// Watches the app's resize feed while the window is running. Resize relayouts
    /// pinned regions without replacing the session controller or its native view.
    /// </summary>
    protected override void OnIsRunningChanged(bool args)
    {
        base.OnIsRunningChanged(args);
        SetHostedApplication(args ? App : null);
    }

    internal void SetHostedApplication(IApplication? app)
    {
        if (ReferenceEquals(_resizeApp, app)) return;
        if (_resizeApp is not null) _resizeApp.ScreenChanged -= OnScreenChanged;
        _resizeApp = app;
        if (app is not null) { app.ScreenChanged += OnScreenChanged; Relayout(); }
    }

    internal void FocusSessionInput()
    {
        SetFocus();
        if (_questionPanel?.SuperView is not null) _questionPanel.SetFocus();
        else if (_panel?.SuperView is not null) _panel.SetFocus();
        else if (_document?.SuperView is not null) _document.SetFocus();
        else if (_commandPaletteView?.SuperView is not null) _commandPaletteView.SetFocus();
        else FocusComposer();
    }

    private void OnScreenChanged(object? sender, EventArgs<Rectangle> args)
    {
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
        if (_relayoutInProgress)
        {
            return;
        }

        _relayoutInProgress = true;
        try
        {
            var driver = _resizeApp?.Driver ?? App?.Driver;
            TgChatLayout.Apply(this, IsSessionPage ? Viewport.Width : driver?.Cols ?? Viewport.Width,
                IsSessionPage ? Viewport.Height : driver?.Rows ?? Viewport.Height);
            Layout();
            SetNeedsDraw();
        }
        finally
        {
            _relayoutInProgress = false;
        }
    }

    private void ShowPanel(Func<int, int, IReadOnlyList<CardLine>> card)
    {
        // A modal panel takes the keyboard; the suggestion list must not linger behind it.
        HideSlashDropdown();
        _modalInputState ??= new ModalInputState(
            Composer.Input.Text?.ToString() ?? string.Empty,
            Composer.Input.InsertionPoint);
        if (_panel is null)
        {
            _panel = new TgPanelHost();
            _panel.PanelKey += OnPanelKey;
            TgModalLayout.Apply(_panel, this, 14);
        }

        _panel.SetCard(card);
        if (_panel.SuperView is null)
        {
            Add(_panel);
        }

        SetNeedsDraw();
        _panel.SetFocus();
    }

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
                    if (choices.Current.Enabled)
                    {
                        ResolveConfirmed(choices.Current.Value);
                    }

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
                case TgPanelKeyKind.Delete when _choicesAllowDelete:
                    // The owner confirms and runs the destructive action; the panel closes
                    // like a confirm so the owner can show its own confirm card next.
                    PanelDeleteRequested?.Invoke(choices.Current.Value);
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
