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
                ? "\uE74D"
                : "\uE895";

    public string DisplayText => IsVerifiedSuccess
        ? "Đã hoàn thành Coding Agent"
        : Status switch
        {
            "succeeded" => "Coding Agent đã chạy xong nhưng chưa xác minh đầy đủ",
            "cancelled" => "Đã dừng Coding Agent",
            "interrupted" => "Coding Agent bị gián đoạn",
            "failed" => "Coding Agent không hoàn thành",
            _ => "Coding Agent đã kết thúc",
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
                parts.Add($"{scanned} file đã quét");
            }

            if (FilesChanged is { } changed)
            {
                parts.Add($"{changed} file thay đổi");
            }

            if (FilesCreated is { } created && created > 0)
            {
                parts.Add($"{created} file tạo mới");
            }

            if (FilesDeleted is { } deleted && deleted > 0)
            {
                parts.Add($"{deleted} file đã xóa");
            }

            if (!IsVerifiedSuccess && VerificationState != "not_applicable")
            {
                parts.Add(VerificationState == "unverified" ? "chưa xác minh" : VerificationState);
            }

            return string.Join(" · ", parts);
        }
    }
}
