using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuApp.Services;

public sealed record AgentActivityModel(
    string SessionId,
    string Status,
    string ToolId,
    string? ErrorCode)
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "type", "session_id", "status", "tool_id", "error_code",
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "requested", "succeeded", "failed", "denied", "approval_required", "cancelled", "timed_out",
    };

    private static readonly Regex SafeCode = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string DisplayText => Status switch
    {
        "requested" => "Đang chuẩn bị thao tác…",
        "succeeded" => "Thao tác đã được xác minh",
        "approval_required" => "Thao tác cần anh xác nhận.",
        "cancelled" => "Thao tác âm lượng đã bị huỷ.",
        "timed_out" => "Thao tác âm lượng đã hết thời gian chờ.",
        _ => $"Thao tác thất bại: {FailureReason(ErrorCode)}",
    };

    public static bool TryParse(JsonElement root, out AgentActivityModel activity)
    {
        activity = new AgentActivityModel("invalid", "failed", "invalid", null);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name) || !observed.Add(property.Name))
            {
                return false;
            }
        }

        if (!TryRequiredString(root, "type", 32, out var type)
            || type != "agent.activity"
            || !TryRequiredString(root, "session_id", 128, out var sessionId)
            || !TryRequiredString(root, "status", 32, out var status)
            || !AllowedStatuses.Contains(status)
            || !TryRequiredString(root, "tool_id", 128, out var toolId)
            || !TryOptionalCode(root, out var errorCode))
        {
            return false;
        }

        activity = new AgentActivityModel(sessionId, status, toolId, errorCode);
        return true;
    }

    private static bool TryRequiredString(
        JsonElement root,
        string property,
        int maximum,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0
            && value.Length <= maximum
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private static bool TryOptionalCode(JsonElement root, out string? value)
    {
        value = null;
        if (!root.TryGetProperty("error_code", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null && SafeCode.IsMatch(value);
    }

    private static string FailureReason(string? code) => code switch
    {
        "capability_unavailable" or "agent_unavailable" =>
            "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này.",
        "scope_not_granted" =>
            "Phiên thao tác này chưa được cấp quyền điều khiển âm lượng.",
        "invalid_arguments" =>
            "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows.",
        "unsupported_platform" =>
            "Điều khiển âm lượng hiện chỉ hỗ trợ Windows.",
        "default_endpoint_unavailable" =>
            "Windows chưa có thiết bị phát mặc định mà em có thể truy cập.",
        "read_failed" => "Em không đọc được trạng thái âm lượng hiện tại.",
        "write_failed" => "Windows không chấp nhận thay đổi âm lượng.",
        "readback_mismatch" =>
            "Windows không xác nhận mức âm lượng sau khi thay đổi.",
        "adapter_error" =>
            "Bộ điều khiển âm lượng gặp lỗi nên em không coi thao tác là thành công.",
        "cancelled" => "Thao tác âm lượng đã bị huỷ.",
        "timed_out" => "Thao tác âm lượng đã hết thời gian chờ.",
        _ => "Không có lý do an toàn cụ thể hơn.",
    };
}
