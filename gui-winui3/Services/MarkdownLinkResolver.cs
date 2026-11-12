using System;
using System.IO;

namespace RuApp.Services;

/// <summary>
/// Maps a Markdown link target to a file path inside the open workspace. The
/// preview itself still validates the path; this resolver only drops anchors,
/// query strings and anything that resolves outside the workspace so a link
/// from model output can never become a workspace escape.
/// </summary>
internal static class MarkdownLinkResolver
{
    /// <summary>
    /// True when the target could name a local file. HTTP(S) and mail links
    /// keep their normal navigation; an anchor-only link names no file.
    /// </summary>
    internal static bool MayBeLocalFile(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string value = target.Trim();
        if (value.StartsWith('#') || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Path.IsPathRooted(value) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile;
    }

    /// <summary>
    /// Resolves a workspace-local target, or null when it is not a local file
    /// inside the workspace. <paramref name="target"/> may be relative
    /// (<c>src/a.cs</c>), workspace-rooted (<c>/src/a.cs</c>), a Windows path or
    /// a <c>file:</c> URI; a <c>#L12</c> fragment and query string are ignored.
    /// </summary>
    internal static string? ResolveLocalFile(string? workspace, string? target)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        string value = target.Trim();
        int fragment = value.IndexOf('#');
        if (fragment >= 0)
        {
            value = value[..fragment];
        }

        int query = value.IndexOf('?');
        if (query >= 0)
        {
            value = value[..query];
        }

        if (value.Length == 0)
        {
            return null;
        }

        string path;
        try
        {
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
                {
                    return null;
                }

                path = fileUri.LocalPath;
            }
            else
            {
                path = Uri.UnescapeDataString(value);
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile && !Path.IsPathRooted(path))
                {
                    // http(s):, mailto: and other schemes are not local files.
                    return null;
                }
            }

            // A single leading slash is the common agent convention for a
            // workspace-root-relative link; a double slash is a UNC path and
            // stays absolute so the workspace check can reject it.
            if (path.StartsWith('/') || path.StartsWith('\\'))
            {
                if (!path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    path = path.TrimStart('/', '\\');
                }
            }

            string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path.TrimStart('/', '\\')));
            string prefix = root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }
}
