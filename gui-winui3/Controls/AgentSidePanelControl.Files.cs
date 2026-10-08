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
        private const int MaxFileTreeRows = 1500;

        public ObservableCollection<FileTreeRow> Files { get; } = new();

        private string? _workspacePath;
        private bool _isFilesLoading;

        public void SetWorkspace(string? path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? null : path;
            if (string.Equals(_workspacePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _workspacePath = normalized;
            _ = RefreshFilesAsync();
        }

        private async void OnFilesRefreshClick(object sender, RoutedEventArgs e)
        {
            await RefreshFilesAsync();
        }

        private async Task RefreshFilesAsync()
        {
            if (_isFilesLoading)
            {
                return;
            }

            _isFilesLoading = true;
            try
            {
                Files.Clear();
                if (_workspacePath is null)
                {
                    FilesRootText.Text = "No workspace";
                    ToolTipService.SetToolTip(FilesRootText, null);
                    ShowFilesStatus("No workspace open.");
                    return;
                }

                FilesRootText.Text = _workspacePath;
                ToolTipService.SetToolTip(FilesRootText, _workspacePath);
                string root = _workspacePath;
                FileTreeListing listing;
                try
                {
                    listing = await Task.Run(() => FileTreeLister.List(root, depth: 0));
                }
                catch (Exception)
                {
                    ShowFilesStatus("Cannot read this folder.");
                    return;
                }

                foreach (var row in listing.Rows)
                {
                    Files.Add(row);
                }

                if (listing.Truncated)
                {
                    Files.Add(FileTreeRow.Note("… more entries not listed", 0));
                }

                ShowFilesStatus(Files.Count == 0 ? "This folder is empty." : null);
            }
            finally
            {
                _isFilesLoading = false;
            }
        }

        private async void OnFileRowClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not FileTreeRow row || !row.CanExpand)
            {
                return;
            }

            if (row.IsExpanded)
            {
                CollapseFileRow(row);
                return;
            }

            row.IsExpanded = true;
            FileTreeListing listing;
            try
            {
                string path = row.FullPath;
                int depth = row.Depth + 1;
                listing = await Task.Run(() => FileTreeLister.List(path, depth));
            }
            catch (Exception)
            {
                row.IsExpanded = false;
                InsertAfter(row, new[] { FileTreeRow.Note("Cannot read this folder.", row.Depth + 1) });
                return;
            }

            if (!row.IsExpanded)
            {
                return;
            }

            InsertAfter(row, BoundFileRows(row, listing));
        }

        private IReadOnlyList<FileTreeRow> BoundFileRows(FileTreeRow parent, FileTreeListing listing)
        {
            int remaining = MaxFileTreeRows - Files.Count;
            if (remaining <= 0)
            {
                return new[] { FileTreeRow.Note("… more entries not listed", parent.Depth + 1) };
            }

            var rows = new List<FileTreeRow>();
            bool overflow = listing.Rows.Count > remaining;
            int take = overflow ? remaining - 1 : listing.Rows.Count;
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
