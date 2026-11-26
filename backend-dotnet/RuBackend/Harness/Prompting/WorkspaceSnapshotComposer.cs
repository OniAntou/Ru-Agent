using System.Text;
using RuAgent.Broker.Tools.Git;
using RuAgent.Harness.Core.Prompting;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Renders one bounded workspace snapshot reference: repository facts reported by the
/// Broker plus a shallow, name-only view of the workspace. It never reads file
/// contents, never follows reparse points and never claims a fact the sources did not
/// produce. Everything it emits is untrusted context under the composer's runtime
/// boundary, and the size cap keeps one listing from crowding out tool results.
/// </summary>
internal static class WorkspaceSnapshotComposer
{
    internal const int MaxEntries = 40;
    internal const int MaxSnapshotBytes = 8 * 1024;
    private const int MaxChangedPaths = 20;

    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".venv", "venv", "node_modules", "bin", "obj", "__pycache__",
        ".pytest_cache", ".ruff_cache", ".mypy_cache", ".idea", ".vscode",
    };

    /// <summary>
    /// Returns the reference body, or null when neither a repository answer nor a
    /// readable workspace layout exists.
    /// </summary>
    internal static string? Render(WorkspaceGitSnapshot? git, string root)
    {
        var builder = new StringBuilder(1024);
        if (git is { IsRepository: false } && !Directory.Exists(root))
        {
            return null;
        }

        if (git is not null)
        {
            AppendRepository(builder, git, root);
        }

        var layout = DescribeLayout(root);
        if (layout is not null)
        {
            builder.Append(layout);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var text = builder.ToString().TrimEnd();
        return text.Length == 0 ? null : FitToBudget(text);
    }

    private static void AppendRepository(StringBuilder builder, WorkspaceGitSnapshot git, string workspaceRoot)
    {
        if (!git.IsRepository)
        {
            builder.AppendLine("repository:");
            builder.AppendLine("  not a git repository (or git could not read it)");
            return;
        }

        builder.AppendLine("repository:");
        if (git.Root is not null)
        {
            var subdirectoryNote = string.Equals(git.Root.TrimEnd('\\', '/'), workspaceRoot.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : " (the workspace is a subdirectory)";
            builder.AppendLine($"  root: {Printable(git.Root)}{subdirectoryNote}");
        }

        builder.AppendLine($"  branch: {Printable(git.Branch ?? "(detached HEAD)")}");
        if (git.HeadCommit is not null)
        {
            builder.AppendLine(
                $"  head: {Printable(git.HeadCommit)}{(git.HeadSubject is null ? string.Empty : " " + Printable(git.HeadSubject))}");
        }

        if (git is { Ahead: not null, Behind: not null })
        {
            builder.AppendLine($"  tracking: ahead {git.Ahead}, behind {git.Behind}");
        }

        builder.Append("working tree: ");
        if (!git.DescribesWorkingTree)
        {
            builder.AppendLine(git.Truncated
                ? "unavailable (git status output was truncated; inspect with git status)"
                : "unavailable (git status failed; inspect with git status)");
        }
        else
        {
            builder.AppendLine(
                $"{git.Modified} modified, {git.Staged} staged, {git.Untracked} untracked, {git.Conflicted} conflicted");
            if (git.HasChangedPaths)
            {
                var shown = git.ChangedPaths.Take(MaxChangedPaths).Select(Printable);
                builder.AppendLine($"  changed paths (workspace-relative): {string.Join(", ", shown)}"
                    + (git.ChangedPaths.Count > MaxChangedPaths ? ", …" : string.Empty));
            }
        }

        if (git.RecentCommits.Count > 0)
        {
            builder.AppendLine("recent commits:");
            foreach (var commit in git.RecentCommits)
            {
                builder.AppendLine($"  - {Printable(commit)}");
            }
        }
    }

    private static string? DescribeLayout(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return DescribeLayoutCore(root);
        }
        catch (Exception)
        {
            // Context only: an unreadable workspace costs the layout section, not the prompt.
            return null;
        }
    }

    private static string? DescribeLayoutCore(string root)
    {
        string[] entries;
        try
        {
            // Bound the enumeration before sorting: a huge or UNC workspace must not
            // cost an unbounded scan just to keep the first forty names.
            entries = Directory.EnumerateFileSystemEntries(root)
                .Take(MaxEntries * 4)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Where(name => !NoiseDirectories.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (Exception)
        {
            return null;
        }

        if (entries.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(512);
        builder.AppendLine("workspace:");
        builder.AppendLine($"  root: {Printable(root)}");
        var rendered = entries.Select(name =>
            Printable(Directory.Exists(Path.Combine(root, name)) ? name + "/" : name));
        builder.AppendLine($"  top-level: {string.Join(", ", rendered)}");

        var markers = DetectMarkers(root, entries);
        if (markers.Count > 0)
        {
            builder.AppendLine($"  project files: {string.Join(", ", markers.Select(Printable))}");
        }

        var commands = DetectCommands(root, entries, markers);
        if (commands.Count > 0)
        {
            builder.AppendLine("candidate commands (detected from files; not run and not verified):");
            foreach (var command in commands)
            {
                builder.AppendLine($"  - {Printable(command)}");
            }
        }

        return builder.ToString();
    }

    private static List<string> DetectMarkers(string root, IReadOnlyList<string> entries)
    {
        var markers = new List<string>();
        foreach (var name in entries)
        {
            if (name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("build.bat", StringComparison.OrdinalIgnoreCase)
                || name.Equals("start_dev.bat", StringComparison.OrdinalIgnoreCase)
                || name.Equals("README.md", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CONTRIBUTING.md", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CONTEXT.md", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(name);
            }
            else if (name.Equals("scripts", StringComparison.OrdinalIgnoreCase)
                || name.Equals("docs", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(name + "/");
            }
        }

        return markers.Take(16).ToList();
    }

    private static List<string> DetectCommands(string root, IReadOnlyList<string> entries, IReadOnlyList<string> markers)
    {
        var commands = new List<string>();
        if (entries.Contains("build.bat", StringComparer.OrdinalIgnoreCase))
        {
            commands.Add(root.Length > 0 ? ".\\build.bat" : "build.bat");
        }

        if (Directory.Exists(Path.Combine(root, "scripts")))
        {
            var testScript = Directory.EnumerateFiles(Path.Combine(root, "scripts"), "test_dotnet.ps1")
                .Any();
            if (testScript)
            {
                commands.Add(".\\scripts\\test_dotnet.ps1 -Configuration Debug");
            }
        }

        var solution = markers.FirstOrDefault(marker =>
            marker.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || marker.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            commands.Add($"dotnet build {solution}");
        }

        if (entries.Contains("package.json", StringComparer.OrdinalIgnoreCase))
        {
            commands.Add("npm test (only if package.json defines it)");
        }

        if (entries.Contains("pyproject.toml", StringComparer.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(root, "tests")))
        {
            commands.Add("pytest (project test layout detected)");
        }

        return commands.Take(4).ToList();
    }

    /// <summary>
    /// Makes one line-safe value for the reference. A filename can carry control
    /// characters; left raw they could forge another line inside the snapshot.
    /// </summary>
    private static string Printable(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character < ' ' || character == '\u007f' ? ' ' : character);
        }

        return builder.ToString();
    }

    /// <summary>Cuts on a character boundary so a path is never split mid-character.</summary>
    private static string FitToBudget(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxSnapshotBytes)
        {
            return text;
        }

        const string note = "\n…(workspace snapshot truncated at its byte budget)";
        var budget = MaxSnapshotBytes - Encoding.UTF8.GetByteCount(note);
        int low = 0, high = text.Length;
        while (high - low > 1)
        {
            var middle = low + ((high - low) / 2);
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) > budget)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        // Never cut a surrogate pair in half: the Core rejects invalid UTF-16.
        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return text[..low] + note;
    }
}
