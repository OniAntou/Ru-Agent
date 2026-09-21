using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The shared bounded read behind every preference store.
/// </summary>
/// <remarks>
/// Four stores had their own copy of this, so the boundary was four places to get wrong.
/// It now lives in one: a file over the cap reads as absent, and the cap is checked as the
/// bytes arrive rather than after the whole file has been buffered.
/// </remarks>
public sealed class PreferenceFileTests
{
    [Fact]
    public void A_file_within_the_cap_is_returned_whole()
    {
        var root = Directory.CreateTempSubdirectory("ru-preference-");
        try
        {
            var path = Path.Combine(root.FullName, "small.json");
            File.WriteAllText(path, "{\"theme\":\"dark\"}");

            var bytes = PreferenceFile.ReadOrNull(path);

            Assert.NotNull(bytes);
            Assert.Equal("{\"theme\":\"dark\"}", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_file_exactly_at_the_cap_is_returned_whole()
    {
        // The bound is inclusive: a store that writes a file of exactly the cap must be
        // able to read it back, or the preference would be written and then silently lost.
        var root = Directory.CreateTempSubdirectory("ru-preference-");
        try
        {
            var path = Path.Combine(root.FullName, "exact.json");
            File.WriteAllBytes(path, new byte[PreferenceFile.MaximumBytes]);

            var bytes = PreferenceFile.ReadOrNull(path);

            Assert.NotNull(bytes);
            Assert.Equal(PreferenceFile.MaximumBytes, bytes.Length);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void One_byte_over_the_cap_reads_as_absent()
    {
        var root = Directory.CreateTempSubdirectory("ru-preference-");
        try
        {
            var path = Path.Combine(root.FullName, "over.json");
            File.WriteAllBytes(path, new byte[PreferenceFile.MaximumBytes + 1]);

            Assert.Null(PreferenceFile.ReadOrNull(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_propagates_to_the_caller_guard()
    {
        // The stores check File.Exists first and wrap the read in a guard that treats an
        // unreadable file as absent. Swallowing inside the reader would make this look like
        // an empty preference instead of a missing file, which is a different answer.
        var missing = Path.Combine(Path.GetTempPath(), "ru-preference-missing-" + Guid.NewGuid().ToString("N"), "x.json");

        Assert.ThrowsAny<IOException>(() => PreferenceFile.ReadOrNull(missing));
    }
}
