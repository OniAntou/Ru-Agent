using RuApp.Models;

namespace RuApp.Services;

/// <summary>Serializes folder reads while retaining the newest refresh request.</summary>
internal sealed class FileTreeRefreshController(
    Func<string, Task<FileTreeListing>> load,
    Action<FileTreeListing?, bool> apply,
    Action<bool>? loadingChanged = null)
{
    private bool _loading;
    private string? _root;
    private string? _retainedRoot;
    private FileTreeListing? _retainedListing;
    internal long Generation { get; private set; }

    // Called on the UI context; continuations keep that context for the apply callback.
    internal async Task RefreshAsync(string? root)
    {
        _root = root;
        Generation++;
        if (_loading) return;
        _loading = true;
        loadingChanged?.Invoke(true);
        try
        {
            while (true)
            {
                var generation = Generation;
                var requestedRoot = _root;
                FileTreeListing? listing = null;
                bool failed = false;
                if (requestedRoot is not null)
                {
                    try { listing = await load(requestedRoot); }
                    catch (Exception) { failed = true; }
                }
                if (generation != Generation) continue;
                if (!failed)
                {
                    _retainedRoot = requestedRoot;
                    _retainedListing = listing;
                }
                else if (string.Equals(requestedRoot, _retainedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    listing = _retainedListing;
                }
                else
                {
                    _retainedRoot = null;
                    _retainedListing = null;
                }
                apply(listing, failed);
                if (generation == Generation) return;
            }
        }
        finally { _loading = false; loadingChanged?.Invoke(false); }
    }
}
