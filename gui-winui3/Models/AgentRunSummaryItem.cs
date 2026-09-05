using RuApp.Services;

namespace RuApp.Models;

/// <summary>
/// Localized, UI-only projection of a typed Agent terminal summary. It deliberately
/// constructs copy from allowlisted fields instead of rendering server text.
/// </summary>
public sealed class AgentRunSummaryItem
{
    public AgentRunSummaryItem(AgentRunSummaryModel summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Status = summary.Status;
        VerificationState = summary.VerificationState;
        WorkspaceLabel = summary.WorkspaceLabel;
        FilesScanned = summary.FilesScanned;
        FilesChanged = summary.FilesChanged;
        FilesCreated = summary.FilesCreated;
        FilesDeleted = summary.FilesDeleted;
    }

    public string Status { get; }
    public string VerificationState { get; }
    public string? WorkspaceLabel { get; }
    public int? FilesScanned { get; }
    public int? FilesChanged { get; }
    public int? FilesCreated { get; }
    public int? FilesDeleted { get; }

    public bool IsVerifiedSuccess => Status == "succeeded" && VerificationState == "verified";
    public bool IsFailure => Status == "failed" || Status == "interrupted";
    public bool IsCancelled => Status == "cancelled";

    public string StatusGlyph => IsVerifiedSuccess
        ? "\uE73E"
        : IsCancelled
            ? "\uE711"
            : IsFailure
                ? "\uEA39"
                : "\uE895";

    public string DisplayText => IsVerifiedSuccess
        ? "Coding Agent completed"
        : Status switch
        {
            "succeeded" => "Coding Agent finished but unverified",
            "cancelled" => "Coding Agent stopped",
            "interrupted" => "Coding Agent interrupted",
            "failed" => "Coding Agent failed",
            _ => "Coding Agent finished",
        };

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(WorkspaceLabel))
            {
                parts.Add(WorkspaceLabel!);
            }

            if (FilesScanned is { } scanned)
            {
                parts.Add(scanned == 1 ? "1 file scanned" : $"{scanned} files scanned");
            }

            if (FilesChanged is { } changed)
            {
                parts.Add(changed == 1 ? "1 file changed" : $"{changed} files changed");
            }

            if (FilesCreated is { } created && created > 0)
            {
                parts.Add(created == 1 ? "1 file created" : $"{created} files created");
            }

            if (FilesDeleted is { } deleted && deleted > 0)
            {
                parts.Add(deleted == 1 ? "1 file deleted" : $"{deleted} files deleted");
            }

            if (!IsVerifiedSuccess && VerificationState != "not_applicable")
            {
                parts.Add(VerificationState == "unverified" ? "unverified" : VerificationState);
            }

            return string.Join(" · ", parts);
        }
    }
}
