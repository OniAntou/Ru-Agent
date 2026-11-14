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
    private const double SlashRowHeight = 44;
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

            SlashCommandList.ItemsSource = matches;
            SlashCommandList.SelectedIndex = 0;
            // The popup takes no layout space; lift it so it sits just above the text box.
            SlashCommandPopup.VerticalOffset = -(matches.Count * SlashRowHeight + 20);
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
