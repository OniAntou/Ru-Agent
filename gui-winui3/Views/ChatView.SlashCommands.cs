using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The composer's slash menu and custom command expansion. Typing <c>/</c> lists the
/// workspace's and the user's <c>.ru/commands</c> templates; sending one replaces it with
/// the template's prompt first, so the turn that leaves is ordinary text.
/// </summary>
public sealed partial class ChatView
{
    // Fixed sizes the menu's XAML uses, so the first frame already sits above the text box;
    // OnSlashCardSizeChanged then snaps the offset to the measured card.
    private const double SlashRowHeight = 34;
    private const double SlashGroupHeaderHeight = 26;
    private const double SlashFooterHeight = 30;
    private const double SlashCardChrome = 2 + 6 + 4; // border plus the list's top and bottom padding
    private const double SlashGapAboveComposer = 8;
    private const double SlashMinWidth = 360;
    private long _slashQuery;

    private static string? SlashUserHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    /// <summary>
    /// Replaces a typed custom command with its prompt in the composer itself, before the
    /// send or queue path snapshots the text. False when the text is not a custom command.
    /// </summary>
    private bool ExpandCustomCommandInComposer()
    {
        if (ComposerSlashCommands.Expand(ChatInputTextBox.Text, _agentWorkspace, SlashUserHome()) is not { } expanded) return false;
        CloseSlashMenu();
        ChatInputTextBox.Text = expanded;
        ChatInputTextBox.SelectionStart = expanded.Length;
        return true;
    }

    private async void OnSlashDraftChanged(object sender, TextChangedEventArgs e)
    {
        var draft = ChatInputTextBox.Text;
        var query = ++_slashQuery;
        if (!ComposerSlashCommands.IsTypingCommand(draft))
        {
            CloseSlashMenu();
            return;
        }

        var workspace = _agentWorkspace;
        try
        {
            // Listing reads the command folders; keep that off the UI thread.
            var matches = await Task.Run(() => ComposerSlashCommands.Suggest(draft, workspace, SlashUserHome()));
            if (query != _slashQuery || ChatInputTextBox.Text != draft) return;
            if (matches.Count == 0)
            {
                CloseSlashMenu();
                return;
            }

            // Group labels only earn their space when more than one source answered.
            var groups = ComposerSlashCommands.Group(matches);
            SlashCommandList.ItemsSource = groups.Count > 1
                ? new Microsoft.UI.Xaml.Data.CollectionViewSource { Source = groups, IsSourceGrouped = true }.View
                : matches;
            SlashCommandList.SelectedIndex = 0;
            // The card shares the composer card's edges, so the two read as one control.
            SlashCommandCard.Width = Math.Max(InputBorder.ActualWidth, SlashMinWidth);
            var headers = groups.Count > 1 ? groups.Count : 0;
            PlaceSlashCard(matches.Count * SlashRowHeight + headers * SlashGroupHeaderHeight + SlashFooterHeight + SlashCardChrome);
            bool opening = !SlashCommandPopup.IsOpen;
            SlashCommandPopup.IsOpen = true;
            // Once per opening: typing more letters narrows a menu that is already there.
            if (opening) UiMotion.Enter(SlashCommandList);
        }
        catch (Exception error)
        {
            CloseSlashMenu();
            GuiDiagnosticWriter.AppendTraceLine($"[Composer] Slash menu failed: {error.GetType().Name}");
        }
    }

    /// <summary>
    /// The popup is laid out at the text box's slot, inside the composer card's border,
    /// padding and any queued-turn or attachment rows above it. Measure that inset so the
    /// menu lines up with the composer's left edge and floats a fixed gap above its top.
    /// </summary>
    private void PlaceSlashCard(double cardHeight)
    {
        // Also runs from the card's SizeChanged, where a throw fails the process fast.
        if (!VisualTransforms.TryTransformPoint(ChatInputTextBox, InputBorder, new Windows.Foundation.Point(0, 0), out var inset)) return;
        SlashCommandPopup.HorizontalOffset = -inset.X;
        SlashCommandPopup.VerticalOffset = -(inset.Y + cardHeight + SlashGapAboveComposer);
    }

    /// <summary>The measured card wins over the estimate (text scaling, a theme's padding).</summary>
    private void OnSlashCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0) PlaceSlashCard(e.NewSize.Height);
    }

    private void OnSlashComposerLostFocus(object sender, RoutedEventArgs e) => CloseSlashMenu();

    private void CloseSlashMenu()
    {
        _slashQuery++;
        if (SlashCommandPopup is not null) SlashCommandPopup.IsOpen = false;
    }

    /// <summary>Arrow keys move, Tab or Enter completes, Escape closes. True when the key was the menu's.</summary>
    private bool TryHandleSlashMenuKey(KeyRoutedEventArgs e)
    {
        if (SlashCommandPopup?.IsOpen != true || SlashCommandList.Items.Count == 0) return false;
        var count = SlashCommandList.Items.Count;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                SlashCommandList.SelectedIndex = (SlashCommandList.SelectedIndex + 1) % count;
                return true;
            case Windows.System.VirtualKey.Up:
                SlashCommandList.SelectedIndex = (SlashCommandList.SelectedIndex + count - 1) % count;
                return true;
            case Windows.System.VirtualKey.Escape:
                CloseSlashMenu();
                return true;
            case Windows.System.VirtualKey.Tab:
            case Windows.System.VirtualKey.Enter:
                if (SlashCommandList.SelectedItem is not ComposerSlashSuggestion chosen) return false;
                // Enter on a fully typed name sends it; only an unfinished name is completed.
                if (e.Key == Windows.System.VirtualKey.Enter && ChatInputTextBox.Text == chosen.Command) return false;
                CompleteSlashCommand(chosen);
                return true;
            default:
                return false;
        }
    }

    private void OnSlashCommandClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ComposerSlashSuggestion chosen) CompleteSlashCommand(chosen);
    }

    private void CompleteSlashCommand(ComposerSlashSuggestion chosen)
    {
        // A built-in takes no arguments; a template usually does, so leave room for them.
        var text = chosen.IsBuiltIn ? chosen.Command : chosen.Command + " ";
        ChatInputTextBox.Text = text;
        ChatInputTextBox.SelectionStart = text.Length;
        CloseSlashMenu();
        ChatInputTextBox.Focus(FocusState.Programmatic);
    }
}
