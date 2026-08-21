using Xunit;

namespace RuApp.Tests;

public sealed class AsciiMeteorFieldContractTests
{
    [Fact]
    public void Chat_hosts_a_noninteractive_meteor_field_and_drives_it_from_messages()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("controls:AsciiMeteorField", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MeteorField\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MeteorField.IsActive = !hasMessages", code, StringComparison.Ordinal);
        Assert.Contains("MeteorField?.Dispose()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meteor_control_is_sparse_ascii_only_and_has_explicit_cleanup()
    {
        var source = File.ReadAllText(FindSource("Controls", "AsciiMeteorField.cs"));

        Assert.Contains("class AsciiMeteorField : Canvas, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMeteorCount = 3", source, StringComparison.Ordinal);
        Assert.Contains("\"..-*\"", source, StringComparison.Ordinal);
        Assert.Contains("\"---*\"", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("private void StopAndClear()", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("Children.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("emoji", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gradient", source, StringComparison.Ordinal);
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
