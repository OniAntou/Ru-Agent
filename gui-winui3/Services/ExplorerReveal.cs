namespace RuApp.Services;

/// <summary>
/// Opens File Explorer at a workspace file. Presentation only: it starts the user's
/// own shell for a path the surface already resolved, and is not a Broker tool path.
/// </summary>
public static class ExplorerReveal
{
    /// <summary>Selects the file, or opens its folder when the file is already gone.</summary>
    public static void Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // A deleted file has no item to select; its folder still exists to show.
        string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false })?.Dispose();
    }
}
