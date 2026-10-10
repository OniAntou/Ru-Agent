using System.Text;
using System.Reflection;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileMutationGuardTests : IDisposable
{
    private const int AtomicSavePreimageBytes = 32 * 1024 * 1024;
    private const int AtomicSaveAttempts = 8;
    private const int AtomicSaveProbeSpins = 200_000;
    private const string EditorContent = "editor-content";

    private readonly string _root;

    public FileMutationGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Full(string name) => Path.Combine(_root, name);

    [Fact]
    public void Oversized_preimage_is_refused_without_mutating_the_file()
    {
        const long oversizedPreimageBytes = 33L * 1024 * 1024;
        var path = Full("large-preimage.txt");
        using (var original = File.Create(path)) original.SetLength(oversizedPreimageBytes);
        var expected = FileHashService.ComputeFileHash(path);

        var error = Assert.Throws<IOException>(() => FileMutationGuard.ReplaceIfHashMatches(
            path, Encoding.UTF8.GetBytes("new"), expected));

        Assert.Equal("file_too_large", error.Message);
        Assert.Equal(oversizedPreimageBytes, new FileInfo(path).Length);
        Assert.Equal(expected, FileHashService.ComputeFileHash(path));
    }

    [Fact]
    public void Replace_preserves_the_validated_file_for_an_existing_reader()
    {
        var path = Full("held-reader.txt");
        File.WriteAllText(path, "before");
        var expected = FileHashService.ComputeFileHash(path);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("after"), expected);

        using var textReader = new StreamReader(reader);
        Assert.Equal("after", textReader.ReadToEnd());
        Assert.Equal("after", File.ReadAllText(path));
    }

    [Fact]
    public void Mutation_handle_denies_editor_rename_over_until_release()
    {
        var path = Full("pinned.txt");
        var editor = Full("editor.tmp");
        File.WriteAllText(path, "before");
        File.WriteAllText(editor, EditorContent);
        var factory = typeof(FileMutationGuard).GetMethod("OpenForMutation", BindingFlags.NonPublic | BindingFlags.Static)!;
        using (var held = (FileStream)factory.Invoke(null, [path, false])!)
        {
            Assert.Throws<IOException>(() => File.Replace(editor, path, null));
            using var textReader = new StreamReader(held, leaveOpen: true);
            Assert.Equal("before", textReader.ReadToEnd());
        }
        File.Replace(editor, path, null);
        Assert.Equal(EditorContent, File.ReadAllText(path));
    }

    [Fact]
    public void Replace_fails_closed_while_another_writer_holds_the_file()
    {
        // The writer handle is open for the whole guard call: this is the window
        // between the preimage hash and the replacement. The guard must refuse
        // instead of silently overwriting the writer's content.
        var path = Full("target.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);
        var newBytes = Encoding.UTF8.GetBytes("v2");

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            writer.Write(Encoding.UTF8.GetBytes("external"));
            writer.Flush();

            var error = Assert.Throws<UnauthorizedAccessException>(
                () => FileMutationGuard.ReplaceIfHashMatches(path, newBytes, expected));
            Assert.Equal("workspace_changed", error.Message);
        }

        Assert.Equal("external", File.ReadAllText(path));

        var externalHash = FileHashService.ComputeFileHash(path);
        FileMutationGuard.ReplaceIfHashMatches(path, newBytes, externalHash);
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void Replace_detects_a_genuine_preimage_change_and_writes_nothing()
    {
        var path = Full("changed.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);
        File.WriteAllText(path, "external");

        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("v2"), expected));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_fails_closed_while_another_writer_holds_the_file()
    {
        var path = Full("delete.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var error = Assert.Throws<UnauthorizedAccessException>(
                () => FileMutationGuard.DeleteIfHashMatches(path, expected));
            Assert.Equal("workspace_changed", error.Message);
        }

        Assert.True(File.Exists(path));
        FileMutationGuard.DeleteIfHashMatches(path, expected);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Replace_with_missing_expected_creates_or_fails_without_clobbering()
    {
        var path = Full("create.txt");
        FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("created"), FileHashService.Missing);
        Assert.Equal("created", File.ReadAllText(path));

        File.WriteAllText(path, "raced");
        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("clobber"), FileHashService.Missing));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("raced", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_with_missing_expected_does_not_remove_a_concurrently_created_file()
    {
        var path = Full("appeared.txt");
        File.WriteAllText(path, "appeared");

        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.DeleteIfHashMatches(path, FileHashService.Missing));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("appeared", File.ReadAllText(path));
    }

    [Fact]
    public void Replace_serializes_an_editor_save_with_the_guarded_update()
    {
        var path = Full("atomic-replace.txt");
        var newBytes = Encoding.UTF8.GetBytes("guard-content");
        AssertAtomicSaveSerialized(path, expected =>
            FileMutationGuard.ReplaceIfHashMatches(path, newBytes, expected), "guard-content");
    }

    [Fact]
    public void Delete_serializes_an_editor_save_with_the_guarded_update()
    {
        var path = Full("atomic-delete.txt");
        AssertAtomicSaveSerialized(path, expected =>
            FileMutationGuard.DeleteIfHashMatches(path, expected), null);
    }

    private void AssertAtomicSaveSerialized(string path, Action<string> mutate, string? guardedContent)
    {
        for (var attempt = 0; attempt < AtomicSaveAttempts; attempt++)
        {
            File.WriteAllBytes(path, new byte[AtomicSavePreimageBytes]);
            var expected = FileHashService.ComputeFileHash(path);
            var swapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            var editor = Task.Run(() => AtomicSaveWhenHeld(path, swapped, stop.Token));

            UnauthorizedAccessException? error = null;
            try
            {
                mutate(expected);
            }
            catch (UnauthorizedAccessException caught)
            {
                error = caught;
            }
            finally
            {
                stop.Cancel();
            }

            editor.GetAwaiter().GetResult();
            if (!swapped.Task.IsCompletedSuccessfully)
            {
                // The editor's write probe can collide with the guard's own open;
                // that attempt is inconclusive, so retry with a fresh preimage.
                continue;
            }

            if (swapped.Task.Result)
            {
                Assert.NotNull(error);
                Assert.Equal("workspace_changed", error!.Message);
                Assert.Equal(EditorContent, File.ReadAllText(path));
            }
            else
            {
                Assert.Null(error);
                if (guardedContent is null) Assert.False(File.Exists(path));
                else Assert.Equal(guardedContent, File.ReadAllText(path));
            }
            return;
        }

        Assert.Fail("the editor never observed the held mutation handle");
    }

    private static void AtomicSaveWhenHeld(string path, TaskCompletionSource<bool> swapped, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                var temp = path + ".editor-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, EditorContent);
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                    swapped.TrySetResult(true);
                }
                catch (IOException saveError) when (saveError.HResult == unchecked((int)0x80070020))
                {
                    File.Delete(temp);
                    swapped.TrySetResult(false);
                }
                catch (FileNotFoundException)
                {
                    // The guard's delete won the race: the destination was gone
                    // before the editor's replace ran. That is a legitimate
                    // "editor did not swap" outcome for the delete variant, not
                    // a harness failure (this flaked under load).
                    File.Delete(temp);
                    swapped.TrySetResult(false);
                }
                return;
            }

            Thread.SpinWait(AtomicSaveProbeSpins);
        }
    }
}
