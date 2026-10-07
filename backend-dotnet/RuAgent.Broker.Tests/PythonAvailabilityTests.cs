using RuAgent.Broker.Skills;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PythonAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-python-availability-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("python.exe")]
    [InlineData("python3.exe")]
    [InlineData("py.exe")]
    public void Interpreter_names_on_path_are_found(string executable)
    {
        var directory = CreateDirectory("found-" + executable);
        File.WriteAllBytes(Path.Combine(directory, executable), [0]);

        Assert.True(PythonAvailability.Probe([directory]));
    }

    [Fact]
    public void Empty_path_is_not_available()
    {
        Assert.False(PythonAvailability.Probe([CreateDirectory("empty")]));
    }

    [Fact]
    public void Scan_checks_the_last_directory_within_the_cap()
    {
        var directories = CreateDirectories(PythonAvailability.MaxSearchDirectories);
        File.WriteAllBytes(Path.Combine(directories[^1], "python.exe"), [0]);

        Assert.True(PythonAvailability.Probe(directories));
    }

    [Fact]
    public void Scan_stops_at_the_bounded_directory_cap()
    {
        var directories = CreateDirectories(PythonAvailability.MaxSearchDirectories + 1);
        File.WriteAllBytes(Path.Combine(directories[^1], "python.exe"), [0]);

        Assert.False(PythonAvailability.Probe(directories));
    }

    private List<string> CreateDirectories(int count)
    {
        var directories = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            directories.Add(CreateDirectory("dir-" + index.ToString()));
        }

        return directories;
    }

    private string CreateDirectory(string leaf)
    {
        var directory = Path.Combine(_root, leaf);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
