using Microsoft.UI.Xaml;

namespace RuApp.Views;

/// <summary>
/// Skills reach the chat from the Customize page: typing <c>/skills</c> opens it, and
/// choosing "Use in chat" there comes back with the skill's command in the composer.
/// </summary>
public sealed partial class ChatView
{
    /// <summary>The user typed <c>/skills</c>; the shell shows the Customize page.</summary>
    public event EventHandler? CustomizeRequested;

    /// <summary>The workspace whose skills the Customize page manages.</summary>
    public string? CurrentWorkspace => _agentWorkspace;

    /// <summary>
    /// Puts <c>/skill &lt;id&gt;</c> at the front of the composer, keeping whatever the user
    /// had typed after an earlier skill command as the skill's arguments.
    /// </summary>
    public void InsertSkillCommand(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        var draft = ChatInputTextBox.Text.Trim();
        if (draft.StartsWith("/skill ", StringComparison.OrdinalIgnoreCase))
        {
            var argumentStart = draft.IndexOf(' ', 7);
            draft = argumentStart < 0 ? string.Empty : draft[(argumentStart + 1)..];
        }
        ChatInputTextBox.Text = $"/skill {skillId} {draft}";
        ChatInputTextBox.SelectionStart = ChatInputTextBox.Text.Length;
        ChatInputTextBox.Focus(FocusState.Programmatic);
    }
}
