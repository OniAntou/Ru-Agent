using RuApp.Models;

namespace RuApp.Services;

/// <summary>Refreshes just the expanded branches of a bounded, presentation-only tree.</summary>
internal static class FileTreeSnapshotLoader
{
    internal const int MaxRows = 1500;

    internal static FileTreeListing Load(string root, IReadOnlySet<string> expanded)
    {
        var rows = new List<FileTreeRow>();
        bool truncated = false;
        bool installationHidden = false;
        Append(root, 0);
        return new(rows, truncated, installationHidden);

        void Append(string directory, int depth)
        {
            FileTreeListing listing;
            try { listing = FileTreeLister.List(directory, depth); }
            catch (Exception ex) when (depth > 0 && ex is IOException or UnauthorizedAccessException)
            {
                Add(FileTreeRow.Note("Cannot read this folder. Collapse and reopen to retry.", depth));
                return;
            }
            installationHidden |= listing.InstallationFilesHidden;
            foreach (var row in listing.Rows)
            {
                if (!Add(row)) return;
                if (row.CanExpand && expanded.Contains(row.FullPath))
                {
                    row.IsExpanded = true;
                    Append(row.FullPath, depth + 1);
                }
            }
            if (listing.Truncated) Add(FileTreeRow.Note("… more entries not listed", depth));
        }

        bool Add(FileTreeRow row)
        {
            if (rows.Count >= MaxRows)
            {
                truncated = true;
                rows[^1] = FileTreeRow.Note("… more entries not listed", row.Depth);
                return false;
            }
            rows.Add(row);
            return true;
        }
    }
}
