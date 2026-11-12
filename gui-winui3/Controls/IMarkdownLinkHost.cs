namespace RuApp.Controls;

/// <summary>
/// The owning surface that can resolve a workspace-local Markdown link. The
/// control only discovers it through the visual tree, so Markdown never gains
/// a second way to read or open files on its own.
/// </summary>
public interface IMarkdownLinkHost
{
    /// <summary>
    /// Opens a link target for the user. Non-file targets are ignored by the
    /// host; a workspace-relative or file: target is shown in the read-only
    /// preview, which enforces workspace bounds, reparse and hidden-path rules.
    /// </summary>
    Task OpenMarkdownLinkAsync(string target);
}
