using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

internal sealed record TgSessionTab(string Title, string State, bool Active);

/// <summary>One runnable terminal window; only the selected session page is visible.</summary>
internal sealed class TgSessionHostWindow : Window
{
    private readonly TgSessionStrip _strip = new();
    private readonly List<TgChatWindow> _pages = [];
    private System.Drawing.Size _lastSize;
    internal TgChatWindow? ActivePage { get; private set; }

    internal TgSessionHostWindow()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
        Add(_strip);
    }

    internal void AddPage(TgChatWindow page)
    {
        page.IsSessionPage = true;
        page.Transcript.DeferWrapping = true;
        page.X = 0; page.Width = Dim.Fill(); page.Height = Dim.Fill();
        page.Visible = false;
        _pages.Add(page);
        Add(page);
    }

    internal void Activate(TgChatWindow page)
    {
        if (!_pages.Contains(page)) throw new InvalidOperationException("session_page_not_registered");
        if (ActivePage is { } previous)
        {
            previous.Landing.ClearRasterWordmark();
            previous.Transcript.DeferWrapping = true;
            previous.Visible = false;
        }
        ActivePage = page;
        page.Transcript.DeferWrapping = false;
        page.Visible = true;
        RefreshPageLayout(true);
        page.FocusSessionInput();
        SetNeedsDraw();
    }

    internal void RemovePage(TgChatWindow page)
    {
        if (ReferenceEquals(ActivePage, page)) throw new InvalidOperationException("cannot_remove_active_session_page");
        _pages.Remove(page);
        Remove(page);
        RefreshPageLayout(true);
    }

    internal void UpdateTabs(IReadOnlyList<TgSessionTab> tabs)
    {
        if (!_strip.SetTabs(tabs)) return;
        _strip.Visible = tabs.Count > 1;
        RefreshPageLayout(true);
    }

    internal void RefreshPageLayout(bool force = false)
    {
        var size = Viewport.Size;
        if (!force && size == _lastSize) return;
        _lastSize = size;
        foreach (var page in _pages) page.Y = _strip.Visible ? 1 : 0;
        Layout();
        ActivePage?.RefreshSessionLayout();
    }

    private sealed class TgSessionStrip : View
    {
        private TgSessionTab[] _tabs = [];
        internal TgSessionStrip() { Height = 1; Width = Dim.Fill(); CanFocus = false; Visible = false; }
        internal bool SetTabs(IReadOnlyList<TgSessionTab> tabs)
        {
            if (_tabs.SequenceEqual(tabs)) return false;
            _tabs = tabs.ToArray(); SetNeedsDraw(); return true;
        }
        protected override bool OnDrawingContent(DrawContext? context)
        {
            var x = 0;
            var width = Viewport.Width;
            var hint = width >= 50 ? " f6 next · alt+n new" : string.Empty;
            var budget = Math.Max(0, width - hint.Length);
            var cellBudget = Math.Clamp(budget / Math.Max(1, Math.Min(3, _tabs.Length)), 8, 24);
            var visibleCount = Math.Max(1, budget / cellBudget);
            var selected = Array.FindIndex(_tabs, tab => tab.Active);
            var start = Math.Clamp(selected - visibleCount + 1, 0, Math.Max(0, _tabs.Length - visibleCount));
            for (var index = start; index < Math.Min(_tabs.Length, start + visibleCount); index++)
            {
                var tab = _tabs[index];
                var label = TuiText.Truncate($" {index + 1}:{tab.State} {tab.Title} ", Math.Min(cellBudget, budget - x));
                if (x + TuiText.VisibleWidth(label) > budget)
                {
                    if (x < budget) { Move(x, 0); AddStr("…"); }
                    break;
                }
                SetAttribute(TgPalette.Attr(tab.Active ? TuiToken.Ink : TuiToken.Meta,
                    tab.Active ? TuiPalette.ElementRgb() : (0, 0, 0)));
                Move(x, 0); AddStr(label); x += TuiText.VisibleWidth(label);
            }
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            if (hint.Length > 0) { Move(width - hint.Length, 0); AddStr(hint); }
            return true;
        }
    }
}
