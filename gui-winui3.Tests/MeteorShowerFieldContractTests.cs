using Xunit;

namespace RuApp.Tests;

public sealed class MeteorShowerFieldContractTests
{
    [Fact]
    public void Chat_hosts_a_noninteractive_meteor_shower_and_drives_it_from_messages()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("controls:MeteorShowerField", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MeteorShowerField\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MeteorShowerField.IsActive = !hasMessages", code, StringComparison.Ordinal);
        Assert.Contains("MeteorShowerField?.Dispose()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AsciiMeteorField", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AsciiMeteorField", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meteor_shower_is_vector_native_fixed_and_disposable()
    {
        var source = File.ReadAllText(FindSource("Controls", "MeteorShowerField.cs"));

        Assert.Contains("class MeteorShowerField : Canvas, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMeteorCount = 5", source, StringComparison.Ordinal);
        Assert.Contains("private static readonly MeteorSpec[] Burst", source, StringComparison.Ordinal);
        Assert.Contains("private sealed class MeteorVisual", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("Stopwatch", source, StringComparison.Ordinal);
        Assert.Contains("Polygon", source, StringComparison.Ordinal);
        Assert.Contains("Line", source, StringComparison.Ordinal);
        Assert.Contains("CompositeTransform", source, StringComparison.Ordinal);
        Assert.Contains("Idle", source, StringComparison.Ordinal);
        Assert.Contains("Burst", source, StringComparison.Ordinal);
        Assert.Contains("Rest", source, StringComparison.Ordinal);
        Assert.Contains("private void StopAndClear()", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("Children.Remove", source, StringComparison.Ordinal);
        Assert.Contains("RectangleGeometry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TextBlock", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ascii", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new Random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Gradient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("particle", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emoji", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(new[] { directory.FullName, "gui-winui3" }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
