using System.Text.RegularExpressions;

namespace RuApp.Services;

/// <summary>
/// The background media library: a picked file is copied under a generated name
/// into <c>%LOCALAPPDATA%\Ru\backgrounds</c>, and the preferences keep only that
/// name. The original path is never stored, so moving or deleting the source file
/// cannot break the background, and a preference file (or an imported theme)
/// cannot point Ru at an arbitrary or network path.
/// </summary>
public sealed partial class AppearanceMediaFiles
{
    public const long MaximumImageBytes = 40L * 1024 * 1024;
    public const long MaximumVideoBytes = 300L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mov", ".wmv", ".mkv"];

    public static IReadOnlyList<string> PickerExtensions { get; } = [.. ImageExtensions, .. VideoExtensions];

    [GeneratedRegex("^[0-9a-f]{32}\\.[a-z0-9]{3,4}$")]
    private static partial Regex GeneratedNamePattern();

    private readonly string _directory;

    public AppearanceMediaFiles(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "backgrounds");
    }

    public static AppearanceBackgroundKind KindOf(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (ImageExtensions.Contains(extension)) return AppearanceBackgroundKind.Image;
        if (VideoExtensions.Contains(extension)) return AppearanceBackgroundKind.Video;
        return AppearanceBackgroundKind.None;
    }

    /// <summary>True only for names this library generates: 32 hex digits and a known extension.</summary>
    public static bool IsGeneratedName(string? fileName) =>
        fileName is not null
        && GeneratedNamePattern().IsMatch(fileName)
        && KindOf(fileName) != AppearanceBackgroundKind.None;

    /// <summary>The full path for a stored name, or null when the name is not one of ours or the file is gone.</summary>
    public string? Resolve(string? fileName)
    {
        if (!IsGeneratedName(fileName)) return null;
        var path = Path.Combine(_directory, fileName!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Copies a picked file into the library. Fails with a user-facing reason for an
    /// unsupported type or a file over the size cap.
    /// </summary>
    public async Task<AppearanceMediaImport> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var kind = KindOf(sourcePath);
        if (kind == AppearanceBackgroundKind.None)
        {
            return AppearanceMediaImport.Failed("That file type isn't supported. Choose an image or a video.");
        }

        var info = new FileInfo(sourcePath);
        if (!info.Exists) return AppearanceMediaImport.Failed("The file couldn't be found.");
        var limit = kind == AppearanceBackgroundKind.Image ? MaximumImageBytes : MaximumVideoBytes;
        if (info.Length > limit)
        {
            return AppearanceMediaImport.Failed(
                $"That file is larger than {limit / (1024 * 1024)} MB. Choose a smaller one.");
        }

        Directory.CreateDirectory(_directory);
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(_directory, name);
        var temporary = target + ".tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporary, target);
            return new AppearanceMediaImport(kind, name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppearanceMediaImport.Failed("Ru couldn't copy that file. Check that it isn't open elsewhere.");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Deletes every stored file except <paramref name="keep"/>; old backgrounds do not pile up.</summary>
    public void DeleteAllExcept(string? keep)
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            if (IsGeneratedName(name) && !string.Equals(name, keep, StringComparison.Ordinal)) TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open (a playing video) is removed on a later change.
        }
    }
}

public sealed record AppearanceMediaImport(AppearanceBackgroundKind Kind, string? FileName, string? Error)
{
    public bool Succeeded => FileName is not null;

    public static AppearanceMediaImport Failed(string error) => new(AppearanceBackgroundKind.None, null, error);
}
