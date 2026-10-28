using System.IO;
using RuAgent.Protocol.Harness;

namespace RuApp.Services
{
    /// <summary>What the header shows for a permission preset: label, dot colour and tooltip.</summary>
    public readonly record struct ChatHeaderPermission(string Label, string DotBrushKey, string ToolTip);

    /// <summary>
    /// Text for the conversation header above the transcript. The header only
    /// reports the session's permission preset; changing it stays in Settings
    /// and the command palette, so nothing here offers a control.
    /// </summary>
    public static class ChatHeaderPresentation
    {
        public const string UntitledSession = "New Chat";
        public const string RunningLabel = "Working";

        private const string ChangeHint = " Change it in Settings or with Ctrl+K.";

        public static string Title(string? title)
            => string.IsNullOrWhiteSpace(title) ? UntitledSession : title.Trim();

        /// <summary>The workspace folder's own name, the breadcrumb's first step.</summary>
        public static string WorkspaceName(string? workspace)
        {
            if (string.IsNullOrWhiteSpace(workspace)) return string.Empty;
            var trimmed = workspace.Trim().TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            // A drive root such as "D:\" has no folder name of its own.
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        // Labels match the preset names in Settings; the dot carries the risk
        // level so the label reads at a glance without a glyph.
        public static ChatHeaderPermission Permission(AgentPermissionPreset preset) => preset switch
        {
            AgentPermissionPreset.Plan => new(
                "Plan", "RuInfoBrush",
                "Permission: Plan. Reads and web run automatically; edits and terminal are refused." + ChangeHint),
            AgentPermissionPreset.FullAccess => new(
                "Full Access", "RuWarningBrush",
                "Permission: Full Access. Commands skip approval prompts." + ChangeHint),
            _ => new(
                "Standard", "RuSuccessBrush",
                "Permission: Standard. Workspace edits and terminal commands run automatically; external directories and explicit rules ask." + ChangeHint),
        };
    }
}
