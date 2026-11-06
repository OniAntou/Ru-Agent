using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RuApp.Controls;

/// <summary>
/// One palette for highlighted code everywhere (chat code blocks, edit diffs and the
/// file preview), built from the app's theme colors so light and dark both stay legible.
/// </summary>
public static class CodeTheme
{
    /// <summary>The foreground for a span kind, or null for the surrounding ink.</summary>
    public static Brush? Foreground(CodeHighlighter.SpanKind kind) => kind switch
    {
        CodeHighlighter.SpanKind.Keyword => ThemeBrush("RuVioletBrush", 126, 34, 206),
        CodeHighlighter.SpanKind.String => ThemeBrush("RuSuccessBrush", 4, 120, 87),
        CodeHighlighter.SpanKind.Comment => ThemeBrush("RuMutedBrush", 88, 101, 121),
        CodeHighlighter.SpanKind.Number => ThemeBrush("RuWarningBrush", 154, 91, 0),
        CodeHighlighter.SpanKind.Function => ThemeBrush("RuInfoBrush", 7, 89, 133),
        CodeHighlighter.SpanKind.Type => ThemeBrush("RuWarningBrush", 154, 91, 0),
        CodeHighlighter.SpanKind.HunkHeader => ThemeBrush("RuInfoBrush", 7, 89, 133),
        CodeHighlighter.SpanKind.Added => ThemeBrush("RuSuccessBrush", 4, 120, 87),
        CodeHighlighter.SpanKind.Removed => ThemeBrush("RuDangerBrush", 185, 28, 28),
        _ => null,
    };

    /// <summary>A run for one highlighted span: colored, and italic for comments.</summary>
    public static Run Run(CodeHighlighter.Span span)
    {
        var run = new Run { Text = span.Text };
        if (Foreground(span.Kind) is { } brush) run.Foreground = brush;
        if (span.Kind == CodeHighlighter.SpanKind.Comment) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        return run;
    }

    /// <summary>The code surface: the sidebar tone, a step darker than the panel it sits in.</summary>
    public static Brush Surface => ThemeBrush("RuSidebarBrush", 226, 226, 226);

    public static Brush Line => ThemeBrush("RuLineBrush", 204, 204, 204);

    public static Brush Ink => ThemeBrush("RuInkBrush", 15, 23, 42);

    public static Brush Muted => ThemeBrush("RuMutedBrush", 88, 101, 121);

    private static SolidColorBrush ThemeBrush(string key, byte r, byte g, byte b) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is SolidColorBrush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(255, r, g, b));
}
