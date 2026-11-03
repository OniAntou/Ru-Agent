using System.Runtime.CompilerServices;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgChatWindow
{
    private sealed class EffectsControl : Button
    {
        // Suppress the default shadow before Button allocates its margin. Clearing
        // ShadowStyle later leaves a bottom margin that consumes a one-row control.
        protected override void OnInitializingShadowStyle(ValueChangingEventArgs<ShadowStyles?> args) => args.NewValue = null;
    }
    private static readonly ConditionalWeakTable<IApplication, TgWelcomeEffectsState> EffectsByApp = new();
    private readonly TgWelcomeEffectsState _unattachedEffects = new();
    private TgWelcomeEffectsState EffectsState => App is { } app ? EffectsByApp.GetValue(app, _ => new()) : _unattachedEffects;
    internal Button EffectsButton { get; private set; } = null!;

    private void AddEffectsButton()
    {
        EffectsButton = new EffectsControl { Text = "FX: On", X = Pos.AnchorEnd(12), Y = 0, Width = 11, Height = 1,
            BorderStyle = Terminal.Gui.Drawing.LineStyle.None, ShadowStyle = ShadowStyles.None, IsDefault = false };
        EffectsButton.SetScheme(TgPalette.Scheme(TuiToken.Meta));
        EffectsButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (!Landing.Visible || HasBlockingOverlay) return;
            EffectsState.Advance();
            RefreshEffects();
            FocusComposer();
        };
        Add(EffectsButton);
    }

    internal void RefreshEffects()
    {
        var mode = EffectsState.Mode;
        Landing.EffectsMode = mode;
        var text = mode switch
        {
            TgWelcomeEffects.Low => "FX: Low",
            TgWelcomeEffects.Off => "FX: Off",
            _ => "FX: On",
        };
        if (EffectsButton.Text != text) EffectsButton.Text = text;
        var enabled = !HasBlockingOverlay;
        if (EffectsButton.Enabled != enabled) EffectsButton.Enabled = enabled;
    }
}
