using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class FileTreeListerTests
{
    [Fact]
    public void Folders_come_first_then_files_case_insensitively()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "zeta"));
            Directory.CreateDirectory(Path.Combine(root, "Alpha"));
            File.WriteAllText(Path.Combine(root, "b.txt"), string.Empty);
            File.WriteAllText(Path.Combine(root, "A.txt"), string.Empty);

            var listing = FileTreeLister.List(root, depth: 0);

            Assert.False(listing.Truncated);
            Assert.Equal(
                new[] { "Alpha", "zeta", "A.txt", "b.txt" },
                listing.Rows.Select(row => row.Name));
            Assert.True(listing.Rows[0].IsDirectory);
            Assert.True(listing.Rows[0].CanExpand);
            Assert.False(listing.Rows[2].IsDirectory);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Build_trees_and_credential_names_are_skipped()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "id_rsa"), string.Empty);
            File.WriteAllText(Path.Combine(root, ".env"), string.Empty);
            File.WriteAllText(Path.Combine(root, "Program.cs"), string.Empty);

            var listing = FileTreeLister.List(root, depth: 0);

            Assert.Equal(new[] { "src", "Program.cs" }, listing.Rows.Select(row => row.Name));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void The_per_directory_cap_keeps_the_sorted_head_and_flags_truncation()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            for (var index = 0; index < 5; index++)
            {
                File.WriteAllText(Path.Combine(root, $"file-{index}.txt"), string.Empty);
            }

            var listing = FileTreeLister.List(root, depth: 0, maxEntries: 2);

            Assert.True(listing.Truncated);
            Assert.Equal(2, listing.Rows.Count);
            Assert.Equal("file-0.txt", listing.Rows[0].Name);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Rows_carry_their_depth_and_icon()
    {
        var row = new FileTreeRow("src", @"C:\x\src", isDirectory: true, canExpand: true, depth: 2);

        Assert.Equal(28, row.IndentMargin.Left);
        Assert.Equal("\uE8B7", row.IconGlyph);

        var note = FileTreeRow.Note("… more entries not listed", 1);

        Assert.True(note.IsNote);
        Assert.Equal(Visibility.Visible, note.NoteVisibility);
        Assert.Equal(Visibility.Collapsed, note.NameVisibility);
        Assert.Equal(string.Empty, note.IconGlyph);
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "ru-file-tree-tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
