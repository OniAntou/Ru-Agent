using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Corner radius is a scale, and every value on it has exactly one name.
/// </summary>
/// <remarks>
/// The GUI had eleven different literals in play at once — 3, 3.5, 4, 5, 6, 8, 9, 10,
/// 12, 13, 16 — spread across panels that are all the same kind of thing. Two panels
/// both reading as "a card" sat at 8 and 12, and the same nav button rounded to 6 in the
/// expanded sidebar and 8 in the collapsed rail, so the shell visibly changed style when
/// it was resized. Nothing flagged it: each literal was locally reasonable.
///
/// So the scale is pinned rather than the individual sites. A new literal is a new step
/// and has to be added here on purpose, next to the step it belongs to; a value that
/// already exists must be spelled as the token that holds it.
/// </remarks>
public sealed class CornerRadiusScaleTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The ladder, lowest step first. Deliberate one-off shapes are not steps and are
    /// listed in <see cref="DeliberateAsymmetricShapes"/> instead.
    /// </summary>
    private static readonly (string Key, string Value)[] Scale =
    {
        ("AppMicroPillCornerRadius", "3"),
        ("AppControlCornerRadius", "6"),
        ("AppInsetCornerRadius", "8"),
        // Was 14, which is the value WinUI's own menus and tooltips use, and it was the
        // only step between a card and a modal. On a 336px popup it read soft: a rounded
        // panel with a rounded border and rounded rows inside it, which is three kinds of
        // round saying the same thing at three sizes. 10 sits between the inset and the
        // card steps, so the ladder stays monotonic and nothing collides.
        ("AppFlyoutCornerRadius", "10"),
        ("AppCardCornerRadius", "12"),
        ("AppModalCornerRadius", "16"),
        ("AppPillCornerRadius", "999"),
    };

    /// <summary>
    /// Corners that pull in on purpose, so they stay off the ladder.
    /// </summary>
    /// <remarks>
    /// Each is a shape rather than a size: a bubble with a tail toward the composer, a
    /// sidebar square against the window edge, a progress bar that must not round at all.
    /// They are listed by value so a change to one of them has to be made here too.
    /// </remarks>
    private static readonly string[] DeliberateAsymmetricShapes =
    {
        "0,6,6,0",     // sidebar: the left edge is the window edge
        "14,14,4,14",  // user bubble: bottom-right pulls in as a tail
        "0",           // progress tracks: a filled square reads as level
    };

    /// <summary>
    /// The only file allowed to spell a radius as a number: it is where the numbers come
    /// from, as the fallback for when the app dictionary is not up yet.
    /// </summary>
    private const string ScaleHelperFile = "UiCornerRadius.cs";

    private static readonly Regex LiteralAttribute =
        new(@"CornerRadius\s*=\s*""([\d.,\s]+)""", RegexOptions.Compiled);

    /// <summary>
    /// A style setter assigns the property by name and the value separately, so it
    /// reads <c>Property="CornerRadius" Value="5"</c> and the attribute form above
    /// cannot see it.
    /// </summary>
    /// <remarks>
    /// Found by a radius that had already been in the model picker for months, five on
    /// a scale whose nearest steps are three and six, sitting in a style the guard
    /// walked past on every run. A guard that only sees one of the two ways a radius
    /// gets written is a guard that reports a clean scale while the scale is drifting.
    /// </remarks>
    private static readonly Regex LiteralSetter =
        new(
            @"Property\s*=\s*""CornerRadius""\s+Value\s*=\s*""([\d.,\s]+)""",
            RegexOptions.Compiled);

    private static readonly Regex LiteralConstructor =
        new(@"new\s+CornerRadius\(\s*([\d.,\s]+?)\s*\)", RegexOptions.Compiled);

    [Fact]
    public void Every_step_is_declared_once_at_its_pinned_value()
    {
        var declared = DeclaredRadii();

        foreach ((string key, string value) in Scale)
        {
            var matches = declared.Where(step => step.Key == key).ToList();

            Assert.True(
                matches.Count == 1,
                $"\"{key}\" must be declared exactly once in App.xaml, found {matches.Count}.");
            Assert.Equal(value, matches[0].Value);
        }
    }

    [Fact]
    public void No_two_steps_share_a_value()
    {
        // Two names for one radius is how a scale starts drifting: someone edits one
        // name, the other silently keeps the old number. Read from the dictionary rather
        // than from the pinned table, so this fails when App.xaml does, not when the
        // table and the dictionary are edited together.
        var duplicates = DeclaredRadii()
            .GroupBy(step => step.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(
                " = ",
                group.Select(step => step.Key).Append(group.Key)))
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "two tokens hold the same radius, so one of them will drift:\n  " +
            string.Join("\n  ", duplicates));
    }

    [Fact]
    public void No_xaml_declares_a_radius_outside_the_scale()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, string value) in Scale)
        {
            allowed.Add(value);
        }

        foreach (string shape in DeliberateAsymmetricShapes)
        {
            allowed.Add(shape);
        }

        var offenders = new List<string>();
        foreach (string file in Sources("*").Where(IsMarkup))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in LiteralAttribute.Matches(text))
            {
                string value = Normalize(match.Groups[1].Value);
                if (!allowed.Contains(value))
                {
                    offenders.Add(
                        $"{Relative(file)}:{LineOf(text, match.Index)} declares CornerRadius=\"{value}\"");
                }
            }

            foreach (Match match in LiteralSetter.Matches(text))
            {
                string value = Normalize(match.Groups[1].Value);
                if (!allowed.Contains(value))
                {
                    offenders.Add(
                        $"{Relative(file)}:{LineOf(text, match.Index)} sets a style CornerRadius to \"{value}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a radius that is not a named step. Add it to the scale next to the step it "
            + "belongs to, or use the token that already holds that value:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_code_behind_constructs_a_radius_from_a_number()
    {
        // Code that builds elements at runtime goes through UiCornerRadius, which reads
        // the same token the markup does. A literal here cannot be changed by editing
        // App.xaml, which is exactly how the two halves drift apart.
        var offenders = new List<string>();
        foreach (string file in Sources("*").Where(IsCode).Where(NotScaleHelper))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in LiteralConstructor.Matches(text))
            {
                string value = Normalize(match.Groups[1].Value);
                if (value.Length == 0)
                {
                    continue;
                }

                offenders.Add(
                    $"{Relative(file)}:{LineOf(text, match.Index)} builds new CornerRadius({value})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "use UiCornerRadius so a radius built in code follows the same scale as the "
            + "markup:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_scale_helper_reads_the_tokens_it_exposes()
    {
        string source = GuiSource.Text("Services", ScaleHelperFile);

        foreach (string key in Scale.Select(step => step.Key))
        {
            if (key is "AppFlyoutCornerRadius" or "AppPillCornerRadius")
            {
                // Only shapes that get built at runtime are exposed. Nothing builds a
                // flyout or a lozenge imperatively, so there is no call site to serve.
                continue;
            }

            Assert.Contains($"\"{key}\"", source, StringComparison.Ordinal);
        }

        // The fallback has to be the pinned number itself, not a re-typed copy that can
        // drift away from the declaration in App.xaml.
        foreach ((string key, string value) in Scale)
        {
            string expected = $"Token(\"{key}\", {value})";
            Assert.True(
                source.Contains(expected, StringComparison.Ordinal)
                    || (key is "AppFlyoutCornerRadius" or "AppPillCornerRadius"),
                $"the fallback for {key} should read: {expected}");
        }
    }

    [Fact]
    public void Every_radius_token_is_read_somewhere()
    {
        // A token nobody reads is a second place to change the value without effect, and
        // it is how a scale grows steps nobody wanted. App.xaml itself is left out: a
        // declaration is not a read, and counting it would pass for every key ever added.
        var corpus = new StringBuilder();
        foreach (string file in Sources("*").Where(path => !IsAppXaml(path)))
        {
            _ = corpus.Append(File.ReadAllText(file));
        }

        string text = corpus.ToString();
        var unread = DeclaredRadii()
            .Where(step => !text.Contains(step.Key, StringComparison.Ordinal))
            .Select(step => step.Key)
            .ToList();

        Assert.True(unread.Count == 0, "declared in App.xaml but never read:\n  " + string.Join("\n  ", unread));
    }

    /// <summary>
    /// Every <c>App*CornerRadius</c> key declared in App.xaml, with the value it was
    /// given. Read from the dictionary so the assertions are about what the app ships,
    /// not about a table repeated inside the test.
    /// </summary>
    private static List<(string Key, string Value)> DeclaredRadii()
    {
        var declared = new List<(string Key, string Value)>();
        var document = XDocument.Parse(GuiSource.Text("App.xaml"));

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "CornerRadius"
                || element.Attribute(Xaml + "Key")?.Value is not { } key
                || !key.StartsWith("App", StringComparison.Ordinal)
                || !key.EndsWith("CornerRadius", StringComparison.Ordinal))
            {
                continue;
            }

            declared.Add((key, element.Value.Trim()));
        }

        return declared;
    }

    private static IEnumerable<string> Sources(string pattern)
    {
        string root = GuiSource.Path();
        char sep = Path.DirectorySeparatorChar;
        return Directory
            .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{sep}.vs{sep}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static bool IsMarkup(string path) =>
        path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    private static bool IsCode(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppXaml(string path) =>
        Path.GetFileName(path).Equals("App.xaml", StringComparison.OrdinalIgnoreCase);

    private static bool NotScaleHelper(string path) =>
        !Path.GetFileName(path).Equals(ScaleHelperFile, StringComparison.Ordinal);

    private static string Normalize(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = parts
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture)
                .ToString("0.####", CultureInfo.InvariantCulture))
            .ToArray();
        return string.Join(',', numbers);
    }

    private static int LineOf(string text, int index) =>
        text[..index].Count(character => character == '\n') + 1;

    private static string Relative(string path) =>
        Path.GetRelativePath(GuiSource.Path(), path).Replace('\\', '/');
}
