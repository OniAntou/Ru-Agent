namespace RuApp.Services;

internal static class AgentFailurePresentation
{
    internal static bool ShouldKeepVisible(string? runStatus, string? safeStatus) =>
        string.Equals(runStatus, "failed", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Render(safeStatus));

    internal static string Render(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || string.Equals(status.Trim(), "Phiên chạy đã kết thúc.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string trimmed = status.Trim();
        if (string.Equals(trimmed, "provider_rate_limited", StringComparison.Ordinal))
        {
            return "Model đang bị giới hạn lượt dùng. Hãy chọn model khác rồi thử lại.";
        }

        if (trimmed.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Coding Agent gặp lỗi. Mở Công cụ → Nhật ký hoạt động để xem trạng thái.";
        }

        string normalized = trimmed.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256] + "…";
    }
}
