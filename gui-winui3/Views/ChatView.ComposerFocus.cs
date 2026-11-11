using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The composer's outline: a hairline at rest that brightens while the text box has
/// focus, so the input reads as the active place without a loud ring.
/// </summary>
public sealed partial class ChatView
{
    private void OnComposerFocusChanged(object sender, RoutedEventArgs e)
    {
        var key = ChatInputTextBox.FocusState == FocusState.Unfocused ? "RuLineBrush" : "RuMutedBrush";
        UiMotion.FadeBrush(InputBorder, Border.BorderBrushProperty, (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key]);
    }
}