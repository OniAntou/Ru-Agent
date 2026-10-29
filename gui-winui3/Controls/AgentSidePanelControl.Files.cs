using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The Files tab: a bounded, lazy workspace tree. Folders load their children
    /// on first expand; the skip sets and per-directory cap live in
    /// <see cref="FileTreeLister"/>, and the whole tree stops at
    /// <see cref="MaxFileTreeRows"/> rows with a note.
    /// </summary>
    public sealed partial class AgentSidePanelControl
    {
        private const int MaxFileTreeRows = FileTreeSnapshotLoader.MaxRows;

        public ObservableCollection<FileTreeRow> Files { get; } = new();

        private string? _workspacePath;
        private FileTreeRefreshController? _filesRefresh;
        private FileTreeRefreshController FilesRefresh => _filesRefresh ??= new(
            root =>
            {
                var expanded = Files.Where(row => row.IsExpanded).Select(row => row.FullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Task.Run(() => FileTreeSnapshotLoader.Load(root, expanded));
            }, ApplyFilesListing, SetFilesLoading);

        private void SetFilesLoading(bool loading)
        {
            FilesLoadingRing.IsActive = loading;
            FilesLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            FilesRefreshButton.IsEnabled = !loading && _workspacePath is not null;
            FilesList.IsEnabled = !loading;
        }

        public void SetWorkspace(string? path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? null : path;
            if (string.Equals(_workspacePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _workspacePath = normalized;
            ClearPreview();
            Files.Clear();
            FilesRootText.Text = normalized ?? "No workspace";
            ToolTipService.SetToolTip(FilesRootText, normalized);
            ShowFilesStatus(normalized is null ? "No workspace open." : "Loading…");
            _ = RefreshFilesAsync();
        }

        private async void OnFilesRefreshClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await RefreshFilesAsync();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[AgentSidePanel] Files refresh failed: {ex.Message}");
            }
        }

        private Task RefreshFilesAsync()
        {
            ShowFilesStatus(Files.Count > 0 ? null : _workspacePath is null ? "No workspace open." : "Loading…");
            return FilesRefresh.RefreshAsync(_workspacePath);
        }

        private void ApplyFilesListing(FileTreeListing? listing, bool failed)
        {
            if (failed && Files.Count > 0)
            {
                ShowFilesStatus("Refresh failed. Showing the previous listing; refresh to retry.");
                return;
            }
            Files.Clear();
            if (listing is null)
            {
                ShowFilesStatus(failed ? "Cannot read this folder." : "No workspace open.");
                return;
            }
            foreach (var row in listing.Rows) Files.Add(row);
            if (listing.Truncated && Files.Count < MaxFileTreeRows) Files.Add(FileTreeRow.Note("… more entries not listed", 0));
            ShowFilesStatus(listing.InstallationFilesHidden
                ? "Application files hidden. Your files remain visible."
                : Files.Count == 0 ? "This folder is empty." : null);
        }

        private async void OnFileRowClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is not FileTreeRow row || row.IsNote)
                {
                    return;
                }

                if (!row.IsDirectory && _workspacePath is { } workspace)
                {
                    OpenPanelTab(SidePanelTabKind.Preview);
                    await FilePreview.OpenAsync(workspace, row.FullPath);
                    return;
                }
                if (!row.CanExpand) return;

                if (row.IsExpanded)
                {
                    CollapseFileRow(row);
                    return;
                }

                row.IsExpanded = true;
                var generation = FilesRefresh.Generation;
                long expansionVersion = row.ExpansionVersion;
                FileTreeListing listing;
                try
                {
                    string path = row.FullPath;
                    int depth = row.Depth + 1;
                    listing = await Task.Run(() => FileTreeLister.List(path, depth));
                }
                catch (Exception)
                {
                    if (generation != FilesRefresh.Generation || expansionVersion != row.ExpansionVersion) return;
                    // Keep the failed branch expanded so collapsing removes its
                    // note before a retry; otherwise each failed reopen adds a row.
                    InsertAfter(row, BoundFileRows(row, new FileTreeListing(
                        [FileTreeRow.Note("Cannot read this folder. Collapse and reopen to retry.", row.Depth + 1)], false)));
                    return;
                }

                if (generation != FilesRefresh.Generation || expansionVersion != row.ExpansionVersion || !row.IsExpanded)
                {
                    return;
                }

                InsertAfter(row, BoundFileRows(row, listing));
            }
            catch (Exception ex)
            {
                // An async void must not throw: a slipped row render stays a
                // trace line instead of an unhandled process crash.
                GuiDiagnosticWriter.AppendTraceLine($"[AgentSidePanel] File row expand failed: {ex.Message}");
            }
        }

        private IReadOnlyList<FileTreeRow> BoundFileRows(FileTreeRow parent, FileTreeListing listing)
        {
            int remaining = MaxFileTreeRows - Files.Count;
            if (remaining <= 0)
            {
                parent.IsExpanded = false;
                ShowFilesStatus("File list limit reached. Collapse other folders or refresh to continue.");
                return Array.Empty<FileTreeRow>();
            }

            var rows = new List<FileTreeRow>();
            bool overflow = listing.Rows.Count > remaining || listing.Truncated;
            int take = Math.Min(listing.Rows.Count, remaining - (overflow ? 1 : 0));
            for (var index = 0; index < take && index < listing.Rows.Count; index++)
            {
                rows.Add(listing.Rows[index]);
            }

            if (overflow || listing.Truncated)
            {
                rows.Add(FileTreeRow.Note("… more entries not listed", parent.Depth + 1));
            }

            return rows;
        }

        private void InsertAfter(FileTreeRow parent, IReadOnlyList<FileTreeRow> rows)
        {
            int index = Files.IndexOf(parent);
            if (index < 0)
            {
                return;
            }

            for (var offset = 0; offset < rows.Count; offset++)
            {
                Files.Insert(index + 1 + offset, rows[offset]);
            }
        }

        private void CollapseFileRow(FileTreeRow row)
        {
            row.IsExpanded = false;
            int index = Files.IndexOf(row);
            if (index < 0)
            {
                return;
            }

            while (index + 1 < Files.Count && Files[index + 1].Depth > row.Depth)
            {
                Files.RemoveAt(index + 1);
            }
        }

        private void ShowFilesStatus(string? message)
        {
            FilesStatusText.Text = message ?? string.Empty;
            FilesStatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
