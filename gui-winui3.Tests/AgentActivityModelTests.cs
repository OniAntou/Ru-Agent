using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityModelTests
{
    [Theory]
    [InlineData("requested", null, "Đang chuẩn bị thao tác…")]
    [InlineData("succeeded", null, "Thao tác đã được xác minh")]
    [InlineData("failed", "write_failed", "Thao tác thất bại: Windows không chấp nhận thay đổi âm lượng.")]
    [InlineData("denied", "scope_not_granted", "Thao tác thất bại: Phiên thao tác này chưa được cấp quyền điều khiển âm lượng.")]
    [InlineData("cancelled", "cancelled", "Thao tác âm lượng đã bị huỷ.")]
    [InlineData("timed_out", "timed_out", "Thao tác âm lượng đã hết thời gian chờ.")]
    public void Activity_frame_maps_only_typed_state_to_safe_copy(
        string status,
        string? errorCode,
        string expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "agent.activity",
            session_id = "session-1",
            status,
            error_code = errorCode,
            tool_id = "system.volume",
        }));

        Assert.True(AgentActivityModel.TryParse(document.RootElement, out var activity));
        Assert.Equal(expected, activity.DisplayText);
    }

    [Fact]
    public void Approval_required_uses_a_bounded_confirmation_message()
    {
        using var document = ParseValid("approval_required", null);

        Assert.True(AgentActivityModel.TryParse(document.RootElement, out var activity));
        Assert.Equal("Thao tác cần anh xác nhận.", activity.DisplayText);
    }

    [Theory]
    [InlineData("capability_unavailable", "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này.")]
    [InlineData("agent_unavailable", "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này.")]
    [InlineData("invalid_arguments", "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows.")]
    [InlineData("unsupported_platform", "Điều khiển âm lượng hiện chỉ hỗ trợ Windows.")]
    [InlineData("default_endpoint_unavailable", "Windows chưa có thiết bị phát mặc định mà em có thể truy cập.")]
    [InlineData("read_failed", "Em không đọc được trạng thái âm lượng hiện tại.")]
    [InlineData("readback_mismatch", "Windows không xác nhận mức âm lượng sau khi thay đổi.")]
    [InlineData("adapter_error", "Bộ điều khiển âm lượng gặp lỗi nên em không coi thao tác là thành công.")]
    [InlineData("future_safe_code", "Không có lý do an toàn cụ thể hơn.")]
    public void Failure_codes_map_to_stable_safe_reasons(string errorCode, string expectedReason)
    {
        using var document = ParseValid("failed", errorCode);

        Assert.True(AgentActivityModel.TryParse(document.RootElement, out var activity));
        Assert.Equal($"Thao tác thất bại: {expectedReason}", activity.DisplayText);
    }

    [Theory]
    [InlineData("chat.activity")]
    [InlineData("")]
    public void Wrong_or_blank_type_is_rejected(string type)
    {
        string json = JsonSerializer.Serialize(new
        {
            type,
            session_id = "session-1",
            status = "requested",
            tool_id = "system.volume",
        });

        AssertRejected(json);
    }

    [Theory]
    [InlineData("session_id", "")]
    [InlineData("session_id", " session-1")]
    [InlineData("tool_id", "")]
    [InlineData("tool_id", "system.volume ")]
    public void Blank_or_untrimmed_identifiers_are_rejected(string property, string value)
    {
        string sessionId = property == "session_id" ? value : "session-1";
        string toolId = property == "tool_id" ? value : "system.volume";
        string json = JsonSerializer.Serialize(new
        {
            type = "agent.activity",
            session_id = sessionId,
            status = "requested",
            tool_id = toolId,
        });

        AssertRejected(json);
    }

    [Theory]
    [InlineData("session_id")]
    [InlineData("tool_id")]
    public void Oversized_identifiers_are_rejected(string property)
    {
        string oversized = new('x', 129);
        string sessionId = property == "session_id" ? oversized : "session-1";
        string toolId = property == "tool_id" ? oversized : "system.volume";
        string json = JsonSerializer.Serialize(new
        {
            type = "agent.activity",
            session_id = sessionId,
            status = "requested",
            tool_id = toolId,
        });

        AssertRejected(json);
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        using var document = ParseValid("running", null);

        Assert.False(AgentActivityModel.TryParse(document.RootElement, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Write Failed")]
    [InlineData("write/failed")]
    [InlineData("write_failed\nprivate")]
    public void Unsafe_error_code_is_rejected(string errorCode)
    {
        using var document = ParseValid("failed", errorCode);

        Assert.False(AgentActivityModel.TryParse(document.RootElement, out _));
    }

    [Fact]
    public void Oversized_error_code_is_rejected()
    {
        using var document = ParseValid("failed", new string('x', 65));

        Assert.False(AgentActivityModel.TryParse(document.RootElement, out _));
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("output")]
    [InlineData("level")]
    [InlineData("device_name")]
    [InlineData("exception")]
    [InlineData("content")]
    public void Raw_or_unexpected_properties_are_rejected(string property)
    {
        string json = $$"""
            {"type":"agent.activity","session_id":"session-1","status":"failed","tool_id":"system.volume","{{property}}":"private"}
            """;

        AssertRejected(json);
    }

    [Fact]
    public void Duplicate_properties_are_rejected()
    {
        const string json = """
            {"type":"agent.activity","session_id":"session-1","status":"requested","status":"succeeded","tool_id":"system.volume"}
            """;

        AssertRejected(json);
    }

    [Fact]
    public void Non_object_frame_is_rejected()
    {
        AssertRejected("[]");
    }

    private static JsonDocument ParseValid(string status, string? errorCode)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "agent.activity",
            session_id = "session-1",
            status,
            tool_id = "system.volume",
            error_code = errorCode,
        }));
    }

    private static void AssertRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(AgentActivityModel.TryParse(document.RootElement, out _));
    }
}
