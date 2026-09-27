using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Services;

/// <summary>
/// The corner-radius scale, for code that builds elements at runtime.
///
/// XAML reads the same values as <c>AppControlCornerRadius</c> and friends from
/// <c>App.xaml</c>; this is the code-behind door to them. Each name is one step
/// of the scale, so two panels of the same kind cannot drift apart.
/// </summary>
public static class UiCornerRadius
{
    /// <summary>Bars 6px tall, where 3 is a fully rounded end.</summary>
    public static CornerRadius MicroPill => Token("AppMicroPillCornerRadius", 3);

    /// <summary>Controls, inputs, badges and chips.</summary>
    public static CornerRadius Control => Token("AppControlCornerRadius", 6);

    /// <summary>A card nested one level inside another surface.</summary>
    public static CornerRadius Inset => Token("AppInsetCornerRadius", 8);

    /// <summary>Standalone panels and cards.</summary>
    public static CornerRadius Card => Token("AppCardCornerRadius", 12);

    /// <summary>The two floating layers: modals and flyouts.</summary>
    public static CornerRadius Modal => Token("AppModalCornerRadius", 16);

    private static CornerRadius Token(string key, double fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true
        && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);
}
