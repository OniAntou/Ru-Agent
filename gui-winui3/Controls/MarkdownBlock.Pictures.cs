using Markdig.Extensions.Mathematics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RuApp.Services;

namespace RuApp.Controls
{
    public sealed partial class MarkdownBlock
    {
        private sealed partial class BlockRenderer
        {
            /// <summary>False while building a table cell: a TextBlock cannot host a picture.</summary>
            private bool _inlinePictures = true;

            private bool CanDraw(SandboxRenderKind kind, string source) =>
                pictures is { Settled: true } && SandboxRenderPolicy.Accepts(kind, source.Trim());

            private Paragraph MathCard(MathBlock math, int indent)
            {
                string source = math.Lines.ToString();
                return math.ClosingFencedCharCount > 0 && CanDraw(SandboxRenderKind.Math, source)
                    ? PictureCard(source, SandboxRenderKind.Math, "math", indent)
                    : CodeCard(source, "math", indent, complete: false);
            }

            /// <summary>
            /// A diagram or display formula drawn by the sandboxed renderer. The card shows
            /// a quiet "Drawing…" line, then the picture; a toggle shows the source and Copy
            /// copies it. When the renderer cannot draw it, the card becomes the ordinary
            /// code card, so nothing the model wrote is ever hidden.
            /// </summary>
            private Paragraph PictureCard(string source, SandboxRenderKind kind, string label, int indent)
            {
                string text = source.TrimEnd('\r', '\n');
                var host = new Grid();
                var header = new Grid { Margin = new Thickness(12, 8, 12, 0) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock
                {
                    Text = label,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = MutedOrDefault(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var sourceToggle = HeaderButton(MonoGlyph("</>"), "Show source");
                var copy = HeaderButton(new FontIcon { Glyph = "", FontSize = 12 }, "Copy source");
                copy.Tag = text;
                copy.Click += OnCopyCodeClick;
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                tools.Children.Add(sourceToggle);
                tools.Children.Add(copy);
                Grid.SetColumn(tools, 1);
                header.Children.Add(tools);

                var body = new ContentControl
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(16, 10, 16, 16),
                    Content = new TextBlock
                    {
                        Text = kind == SandboxRenderKind.Math ? "Typesetting…" : "Drawing diagram…",
                        FontSize = 12,
                        Foreground = MutedOrDefault(),
                    },
                };
                var frame = new StackPanel();
                frame.Children.Add(header);
                frame.Children.Add(body);
                var card = new Border
                {
                    Background = (Brush?)Application.Current?.Resources["RuBackdropSurfaceBrush"],
                    BorderBrush = ThemeBrush("RuLineBrush", 203, 213, 225),
                    BorderThickness = new Thickness(1),
                    CornerRadius = Services.UiCornerRadius.Inset,
                    Child = frame,
                };
                RevealOnHover(card, copy);
                RevealOnHover(card, sourceToggle);
                host.Children.Add(card);
                fitBlock(host, indent * 20);

                _ = DrawAsync();
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = host });
                return paragraph;

                async Task DrawAsync()
                {
                    try
                    {
                        await ShowPictureAsync();
                    }
                    catch (Exception error)
                    {
                        Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] diagram failed: {error.GetType().Name}");
                    }
                }

                async Task ShowPictureAsync()
                {
                    var style = pictures!.Style with { Display = true };
                    var picture = await SandboxRenderer.Shared.RenderAsync(kind, text.Trim(), style);
                    var image = picture is null ? null : await DecodeAsync(picture);
                    if (image is null)
                    {
                        // Nothing to draw: the card turns into the plain source card.
                        host.Children.Clear();
                        host.Children.Add(CodeCardElement(text, label, complete: false));
                        return;
                    }

                    // Fitted to the column down to MinDiagramScale; a wider picture scrolls
                    // sideways rather than shrinking its labels out of reading.
                    var drawn = new Image
                    {
                        Source = image,
                        Stretch = Stretch.Uniform,
                        Width = picture!.Width,
                        Height = picture.Height,
                    };
                    void Fit()
                    {
                        double available = body.ActualWidth - body.Padding.Left - body.Padding.Right;
                        drawn.Width = MarkdownPictures.DiagramWidth(picture.Width, available);
                        drawn.Height = drawn.Width * picture.Height / picture.Width;
                    }

                    body.SizeChanged += (_, _) => Fit();
                    Fit();
                    drawn.SetValue(AutomationProperties.NameProperty, kind == SandboxRenderKind.Math ? "Formula: " + text : "Diagram");
                    var view = new ScrollViewer
                    {
                        Content = drawn,
                        HorizontalAlignment = kind == SandboxRenderKind.Math ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollMode = ScrollMode.Enabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollMode = ScrollMode.Disabled,
                    };
                    var sourceView = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = text,
                            FontFamily = MonoFont,
                            FontSize = MonoFontSize,
                            IsTextSelectionEnabled = true,
                            LineHeight = Math.Round(MonoFontSize * 1.6),
                        },
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollMode = ScrollMode.Enabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollMode = ScrollMode.Disabled,
                    };
                    bool showingSource = false;
                    body.Content = view;
                    sourceToggle.Click += (_, _) =>
                    {
                        showingSource = !showingSource;
                        body.Content = showingSource ? sourceView : view;
                        SetToggle(sourceToggle, showingSource, showingSource ? "Show picture" : "Show source");
                    };
                }
            }

            /// <summary>
            /// Inline math: the source run shows first and is replaced in place by the
            /// picture, which sits on the text baseline. A failed render leaves the run.
            /// </summary>
            private void AddMath(InlineCollection target, MathInline math, Run fallback, double size)
            {
                target.Add(fallback);
                string source = math.Content.ToString().Trim();
                if (!_inlinePictures || !CanDraw(SandboxRenderKind.Math, source)) return;
                _ = DrawAsync();

                async Task DrawAsync()
                {
                    try
                    {
                        await ShowPictureAsync();
                    }
                    catch (Exception error)
                    {
                        Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] formula failed: {error.GetType().Name}");
                    }
                }

                async Task ShowPictureAsync()
                {
                    var style = pictures!.Style with { FontSize = size, Display = math.DelimiterCount > 1 };
                    var picture = await SandboxRenderer.Shared.RenderAsync(SandboxRenderKind.Math, source, style);
                    var image = picture is null ? null : await DecodeAsync(picture);
                    int index = target.IndexOf(fallback);
                    if (image is null || index < 0) return;
                    var view = new Image
                    {
                        Source = image,
                        Width = picture!.Width,
                        Height = picture.Height,
                        Stretch = Stretch.Fill,
                        Margin = new Thickness(1, 0, 1, 0),
                        // The picture's bottom sits on the baseline; move its depth below it.
                        RenderTransform = new TranslateTransform { Y = picture.Depth },
                    };
                    view.SetValue(AutomationProperties.NameProperty, "Formula: " + source);
                    // Insert first: if the picture cannot be placed, the source run stays.
                    target.Insert(index, new InlineUIContainer { Child = view });
                    target.Remove(fallback);
                }
            }

            private static async Task<BitmapImage?> DecodeAsync(SandboxPicture picture)
            {
                try
                {
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(picture.Png);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }

                    stream.Seek(0);
                    var image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    return image;
                }
                catch (Exception error)
                {
                    Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] picture decode failed: {error.GetType().Name}");
                    return null;
                }
            }
        }
    }
}
