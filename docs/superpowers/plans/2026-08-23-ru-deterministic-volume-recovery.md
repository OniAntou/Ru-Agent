# Ru Deterministic Volume Recovery Implementation Plan

> **Status:** Historical implementation record. Deterministic volume recovery
> shipped; current capability truth is in `docs/operations/agent-tools.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make explicit Vietnamese and English volume commands execute through Ru's verified Broker path without model selection, preserve one bounded failure reason for an immediate causal follow-up, and show only truthful Agent activity states.

**Architecture:** A pure Python parser resolves one volume intent before ordinary generation. The Worker emits the existing typed tool request, validates the Broker readback, and returns deterministic visible copy plus an optional bounded `action_outcome`; the ASP.NET Core Host carries a failed outcome through a session-keyed one-shot in-memory store, while WinUI renders typed Agent states instead of raw activity text. Core Audio remains exclusively owned by RuAgentBroker.

**Tech Stack:** Python 3.11-3.14, pytest, .NET 10, ASP.NET Core WebSockets, System.Text.Json, xUnit, WinUI 3, and the deterministic GUI Harness.

## Global Constraints

- Work directly on `main` in `D:\LLM`, preserving unrelated user changes if any appear.
- Use red-green-refactor for every behavior seam. Run heavy Python, .NET, WinUI, publish, and Harness gates sequentially to avoid Windows page-file failures.
- Do not call the configured model for explicit volume execution, deterministic clarification, deterministic action result rendering, or the immediate bounded causal follow-up.
- Do not move native volume access out of RuAgentBroker or add another Windows audio mutation path.
- Visible success is legal only after a matching receipt has `status: succeeded`, `verification_state: verified`, and a strictly valid `system.volume@1.0.0` readback object.
- Never persist prompts, arguments, requested or observed levels, output, endpoint data, device names, native exceptions, credentials, or model selection text in diagnostics or `action_outcome`.
- Keep Unicode emoji and ASCII emoticons out of Ru chat copy.
- Automated checks do not claim that Windows volume audibly changed. Native acceptance remains an explicit user-run check.

## File and Responsibility Map

| File | Responsibility |
| --- | --- |
| `src/ru_runtime/generation/volume_intent.py` | Pure, fail-closed Vietnamese/English volume intent parser. |
| `tests/test_volume_intent.py` | Table-driven parser semantics, ambiguity, exclusion, and bounds. |
| `src/ru_runtime/worker/v1/action_outcome.py` | Strict bounded outcome type, causal follow-up recognition, volume readback parsing, and deterministic public copy. |
| `src/ru_runtime/worker/v1/action_loop.py` | Existing capability/receipt validation; retain validated readback privately for deterministic rendering. |
| `src/ru_runtime/worker/v1/handlers.py` | Orchestrate deterministic intent, one typed request, receipt handling, diagnostics, and normal model fallback. |
| `src/ru_runtime/worker/v1/protocol.py` | Strict `chat.done.action_outcome` validation. |
| `src/ru_runtime/persistence/diagnostic_schema.py` | Allow the three bounded pre-dispatch intent event codes. |
| `tests/test_worker_action_outcome.py` | Outcome parser, public reason mapping, readback validation, and causal follow-up tests. |
| `tests/test_worker_action_loop.py` | Receipt proof and private validated readback tests. |
| `tests/test_worker_runtime_handlers.py` | Worker integration, exact model-call counts, diagnostics, and no-false-success tests. |
| `tests/test_worker_protocol.py` | Python Worker envelope acceptance/rejection for bounded action outcomes. |
| `tests/test_diagnostic_schema.py`, `tests/test_privacy_logging.py` | Intent event allowlist and forbidden-field privacy coverage. |
| `backend-dotnet/RuBackend/Worker/WorkerActionOutcome.cs` | C# strict codec for `action_outcome` and `last_action_outcome`. |
| `backend-dotnet/RuBackend/Worker/WorkerModels.cs` | Optional typed previous outcome on `WorkerChatRequest`. |
| `backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs` | Reject malformed Worker `chat.done` outcomes at the Host boundary. |
| `backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs` | Serialize the optional one-shot outcome into `chat.start`. |
| `backend-dotnet/RuBackend/Conversation/ActionOutcomeContextStore.cs` | Thread-safe session-keyed replace/take/remove in-memory store. |
| `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs` | Consume previous outcome before the turn and retain only failed/clarification terminal outcomes. |
| `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs` | Clear one-shot context on session replacement and disconnect. |
| `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs` | Clear one-shot context after successful session deletion. |
| `backend-dotnet/RuBackend/Program.cs` | Register the singleton context store. |
| `backend-dotnet/RuBackend.Tests/Worker/WorkerActionOutcomeTests.cs` | C# codec and bounds tests. |
| `backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs` | `chat.done` protocol tests. |
| `backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs` | `last_action_outcome` wire-payload tests. |
| `backend-dotnet/RuBackend.Tests/Conversation/ActionOutcomeContextStoreTests.cs` | One-shot, replacement, and session-isolation tests. |
| `backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs` | Host continuity and non-persistence tests. |
| `backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs` | Switch/disconnect cleanup tests. |
| `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs` | Session-deletion cleanup test. |
| `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs` | Optional test injection for the context store. |
| `gui-winui3/Services/AgentActivityModel.cs` | Strict `agent.activity` parser and safe Vietnamese state renderer. |
| `gui-winui3/Services/AgentActivityItem.cs` | Delete this unused superseded record. |
| `gui-winui3/Services/ChatWebSocketClient.cs` | Publish typed Agent activity instead of raw content. |
| `gui-winui3/Views/ChatView.xaml.cs` | Render honest idle, requested, granted, terminal, stale, and disconnected states. |
| `gui-winui3.Tests/AgentActivityModelTests.cs` | Typed frame, failure mapping, malformed frame, and bounds tests. |
| `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs` | Require new copy and ban the old Host-waiting claim. |
| `gui-winui3.Harness/UiAutomationScenario.cs` | Wait for the honest idle Agent-mode label. |
| `scripts/harness/deterministic_worker.py`, `tests/test_agent_harness.py` | Exercise bounded failed `action_outcome` without native audio access. |
| `docs/operations/agent-tools.md` | Deterministic-first selection and verified result behavior. |
| `docs/operations/runtime-behavior.md` | Worker-Host continuity and truthful Agent UI state flow. |
| `docs/operations/logging.md` | Bounded pre-dispatch intent diagnostics. |
| `docs/operations/verification.md` | Focused tests, deterministic Harness evidence, and manual native boundary. |

---

### Task 1: Add the Pure Deterministic Volume Intent Parser

**Files:**
- Create: `src/ru_runtime/generation/volume_intent.py`
- Create: `tests/test_volume_intent.py`

- [ ] **Step 1: Write the failing parser contract tests**

Create table-driven tests with these exact public outcomes and arguments:

```python
from __future__ import annotations

import pytest

from ru_runtime.generation.volume_intent import resolve_volume_intent


@pytest.mark.parametrize(
    ("prompt", "arguments"),
    (
        ("Giảm âm lượng của anh xuống còn 2", {"operation": "set", "level": 2}),
        ("Đặt âm lượng về 0%", {"operation": "set", "level": 0}),
        ("Set the volume to 100 percent", {"operation": "set", "level": 100}),
        ("Tăng âm lượng thêm 5", {"operation": "increase", "amount": 5}),
        ("Giảm âm lượng đi 1%", {"operation": "decrease", "amount": 1}),
        ("Decrease the volume by 50 percent", {"operation": "decrease", "amount": 50}),
        ("Tắt tiếng máy giúp anh", {"operation": "mute"}),
        ("Bật lại tiếng máy", {"operation": "unmute"}),
        ("Âm lượng hiện tại là bao nhiêu?", {"operation": "get"}),
        ("What is the current volume?", {"operation": "get"}),
    ),
)
def test_explicit_commands_resolve_to_one_exact_tool_call(prompt: str, arguments: dict[str, object]) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "execute"
    assert decision.reason_code is None
    assert decision.tool_call is not None
    assert decision.tool_call.tool_id == "system.volume"
    assert decision.tool_call.tool_version == "1.0.0"
    assert decision.tool_call.arguments == arguments


@pytest.mark.parametrize(
    ("prompt", "reason_code"),
    (
        ("Giảm âm lượng 2%", "volume_command_ambiguous"),
        ("Tăng âm lượng", "volume_command_ambiguous"),
        ("Đặt âm lượng về 101", "volume_value_out_of_range"),
        ("Đặt âm lượng về -1", "volume_value_out_of_range"),
        ("Tăng âm lượng thêm 0", "volume_value_out_of_range"),
        ("Giảm âm lượng đi 51", "volume_value_out_of_range"),
        ("Đặt âm lượng về 2.5", "volume_command_ambiguous"),
        ("Tắt tiếng rồi bật tiếng", "volume_command_ambiguous"),
        ("Em chỉnh âm lượng được không?", "volume_capability_question"),
    ),
)
def test_unclear_or_invalid_volume_requests_never_execute(prompt: str, reason_code: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "clarification_required"
    assert decision.reason_code == reason_code
    assert decision.tool_call is None


@pytest.mark.parametrize(
    "prompt",
    (
        "Đừng giảm âm lượng",
        "Nếu giảm âm lượng xuống 2 thì sao?",
        "Giải thích câu 'giảm âm lượng xuống còn 2'",
        "Anh đang nói về volume trong Docker",
        "Bài hát này có âm lượng cảm xúc lớn",
        "Hôm nay em thế nào?",
    ),
)
def test_negated_quoted_hypothetical_and_ordinary_prose_stays_non_action(prompt: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "not_action"
    assert decision.reason_code is None
    assert decision.tool_call is None
```

- [ ] **Step 2: Run the parser tests and verify red**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_intent.py
```

Expected: collection fails with `ModuleNotFoundError: No module named 'ru_runtime.generation.volume_intent'`.

- [ ] **Step 3: Implement the immutable result and fail-closed grammar**

Create this public surface and keep all pattern helpers private:

```python
from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Literal

from ru_runtime.generation.tool_calls import ToolCall

VolumeIntentKind = Literal["not_action", "clarification_required", "execute"]

_NUMBER = r"(?P<value>-?\d+(?:[.,]\d+)?)"
_VOLUME_TOPIC = re.compile(r"(?<!\w)(?:âm lượng|tiếng máy|tiếng|volume|sound)(?!\w)")
_NON_ACTION_PREFIX = re.compile(
    r"^(?:đừng|chớ|không được|nếu|giả sử|giải thích|phân tích|do not|don't|dont|if|hypothetically|explain)\b"
)
_CAPABILITY_QUESTION = re.compile(
    r"(?:được không|có thể .* không|can you|are you able|do you have access)"
)
_COMMAND_WORD = re.compile(
    r"(?:đặt|chỉnh|hạ|giảm|tăng|tắt tiếng|bật tiếng|set|lower|decrease|increase|mute|unmute)"
)
_COMMAND_CONNECTOR = re.compile(r"(?<!\w)(?:và|rồi|and|then)(?!\w)")
_ABSOLUTE_PATTERNS = (
    re.compile(
        rf"(?:đặt|chỉnh|hạ|giảm).*?(?:âm lượng|tiếng máy|tiếng).*?(?:xuống\s+)?(?:còn|về|tới|đến|ở mức)\s*{_NUMBER}\s*%?"
    ),
    re.compile(rf"(?:set|lower|turn down).*?(?:volume|sound).*?(?:to|at)\s*{_NUMBER}\s*(?:%|percent)?"),
)
_RELATIVE_PATTERNS = (
    ("increase", re.compile(rf"(?:tăng).*?(?:âm lượng|tiếng máy|tiếng).*?(?:lên|thêm)\s*{_NUMBER}\s*%?")),
    ("decrease", re.compile(rf"(?:giảm|hạ).*?(?:âm lượng|tiếng máy|tiếng).*?(?:đi|bớt)\s*{_NUMBER}\s*%?")),
    ("increase", re.compile(rf"increase.*?(?:volume|sound).*?by\s*{_NUMBER}\s*(?:%|percent)?")),
    ("decrease", re.compile(rf"(?:decrease|lower).*?(?:volume|sound).*?by\s*{_NUMBER}\s*(?:%|percent)?")),
)
_GET = re.compile(
    r"(?:âm lượng|tiếng máy).*(?:hiện tại|bao nhiêu)|(?:current|what is the).*?(?:volume|sound)"
)
_UNMUTE = re.compile(r"(?:bật lại tiếng|bật tiếng|bỏ tắt tiếng|unmute)")
_MUTE = re.compile(r"(?:tắt tiếng|mute)")
_ACTION_WORD = re.compile(r"(?:đặt|chỉnh|hạ|giảm|tăng|tắt|bật|set|lower|decrease|increase|mute|unmute)")


@dataclass(frozen=True, slots=True)
class VolumeIntentDecision:
    kind: VolumeIntentKind
    tool_call: ToolCall | None = None
    reason_code: str | None = None

    def __post_init__(self) -> None:
        if self.kind == "execute" and (self.tool_call is None or self.reason_code is not None):
            raise ValueError("invalid execute decision")
        if self.kind == "clarification_required" and (self.tool_call is not None or self.reason_code is None):
            raise ValueError("invalid clarification decision")
        if self.kind == "not_action" and (self.tool_call is not None or self.reason_code is not None):
            raise ValueError("invalid non-action decision")


def resolve_volume_intent(prompt: str) -> VolumeIntentDecision:
    if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 16_000:
        return VolumeIntentDecision("not_action")
    text = " ".join(unicodedata.normalize("NFKC", prompt).casefold().split())
    if _NON_ACTION_PREFIX.search(text) or _contains_quoted_volume_command(text):
        return VolumeIntentDecision("not_action")
    if _COMMAND_CONNECTOR.search(text) and len(_COMMAND_WORD.findall(text)) > 1:
        return VolumeIntentDecision("clarification_required", reason_code="volume_command_ambiguous")

    for pattern in _ABSOLUTE_PATTERNS:
        if match := pattern.search(text):
            return _numeric_decision("set", "level", match.group("value"), 0, 100)
    for operation, pattern in _RELATIVE_PATTERNS:
        if match := pattern.search(text):
            return _numeric_decision(operation, "amount", match.group("value"), 1, 50)
    if _GET.search(text):
        return _execute({"operation": "get"})
    if _UNMUTE.search(text):
        return _execute({"operation": "unmute"})
    if _MUTE.search(text):
        return _execute({"operation": "mute"})
    if not _VOLUME_TOPIC.search(text):
        return VolumeIntentDecision("not_action")
    if _CAPABILITY_QUESTION.search(text):
        return VolumeIntentDecision("clarification_required", reason_code="volume_capability_question")
    if _ACTION_WORD.search(text):
        return VolumeIntentDecision("clarification_required", reason_code="volume_command_ambiguous")
    return VolumeIntentDecision("not_action")


def _numeric_decision(
    operation: str,
    field: str,
    raw_value: str,
    minimum: int,
    maximum: int,
) -> VolumeIntentDecision:
    if "." in raw_value or "," in raw_value:
        return VolumeIntentDecision("clarification_required", reason_code="volume_command_ambiguous")
    value = int(raw_value)
    if not minimum <= value <= maximum:
        return VolumeIntentDecision("clarification_required", reason_code="volume_value_out_of_range")
    return _execute({"operation": operation, field: value})


def _execute(arguments: dict[str, object]) -> VolumeIntentDecision:
    return VolumeIntentDecision(
        "execute",
        tool_call=ToolCall("system.volume", "1.0.0", arguments),
    )


def _contains_quoted_volume_command(text: str) -> bool:
    return (
        _VOLUME_TOPIC.search(text) is not None
        and (
            re.search(r'"[^"\n]+"', text) is not None
            or re.search(r"'[^'\n]+'", text) is not None
            or re.search(r"“[^”\n]+”", text) is not None
        )
    )
```

After the first green run, add explicit tests for repeated absolute commands, conflicting absolute/relative commands, boolean-like values, punctuation/case normalization, and every boundary immediately outside the accepted ranges. Route all command-shaped unknowns to clarification and all discussion-shaped unknowns to `not_action`.

- [ ] **Step 4: Run the parser suite and verify green**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_intent.py
```

Expected: all parser cases pass and no test imports Worker, Host, Broker, or native audio code.

- [ ] **Step 5: Commit the parser slice**

```powershell
git add -- src/ru_runtime/generation/volume_intent.py tests/test_volume_intent.py
git commit -m "feat: parse volume commands deterministically"
```

---

### Task 2: Add Bounded Action Outcomes and Deterministic Public Copy

**Files:**
- Create: `src/ru_runtime/worker/v1/action_outcome.py`
- Create: `tests/test_worker_action_outcome.py`

- [ ] **Step 1: Write failing tests for strict outcomes, readback, and public copy**

Use exact schema fields and user-visible strings:

```python
from __future__ import annotations

import pytest

from ru_runtime.worker.v1.action_outcome import (
    ActionOutcomeError,
    is_causal_follow_up,
    parse_action_outcome,
    parse_volume_readback,
    render_action_explanation,
    render_volume_clarification,
    render_volume_failure,
    render_volume_success,
)


def test_action_outcome_round_trips_only_bounded_metadata() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "readback_mismatch",
        }
    )

    assert outcome.to_payload() == {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "readback_mismatch",
    }
    assert "level" not in repr(outcome)


@pytest.mark.parametrize("extra", ("arguments", "output", "prompt", "device_name", "exception"))
def test_action_outcome_rejects_private_or_unknown_fields(extra: str) -> None:
    payload = {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "write_failed",
        extra: "private",
    }

    with pytest.raises(ActionOutcomeError, match="invalid_action_outcome"):
        parse_action_outcome(payload)


@pytest.mark.parametrize("prompt", ("sao vậy?", "tại sao?", "vì sao?", "sao không được?", "why?", "why not?"))
def test_short_causal_follow_up_is_exact_and_bounded(prompt: str) -> None:
    assert is_causal_follow_up(prompt) is True


def test_unrelated_follow_up_does_not_consume_worker_model_path() -> None:
    assert is_causal_follow_up("Sao hôm nay trời nóng vậy?") is False


def test_verified_readback_renders_without_model_prose() -> None:
    readback = parse_volume_readback(
        "set",
        {"operation": "set", "level": 2, "muted": False},
    )

    assert render_volume_success(readback) == "Em đã đặt âm lượng về 2% rồi anh."
    assert render_volume_failure("readback_mismatch") == (
        "Windows không xác nhận mức âm lượng sau khi thay đổi."
    )
    assert render_volume_clarification("volume_command_ambiguous") == (
        "Anh muốn đặt âm lượng về bao nhiêu phần trăm, hay tăng hoặc giảm bao nhiêu?"
    )


def test_action_explanation_uses_the_same_safe_reason_map() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "default_endpoint_unavailable",
        }
    )

    assert render_action_explanation(outcome) == (
        "Vì Windows chưa có thiết bị phát mặc định mà em có thể truy cập."
    )
```

- [ ] **Step 2: Run the outcome tests and verify red**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_action_outcome.py
```

Expected: collection fails because `action_outcome.py` does not exist.

- [ ] **Step 3: Implement the strict outcome and renderer module**

Use these exact bounded statuses and fields:

```python
from __future__ import annotations

import re
import unicodedata
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal

ActionOutcomeStatus = Literal[
    "succeeded",
    "failed",
    "denied",
    "approval_required",
    "cancelled",
    "timed_out",
    "clarification_required",
]

_FIELDS = frozenset(("schema_version", "tool_id", "tool_version", "status", "reason_code"))
_STATUSES = frozenset(
    ("succeeded", "failed", "denied", "approval_required", "cancelled", "timed_out", "clarification_required")
)
_SAFE_CODE = re.compile(r"^[a-z0-9][a-z0-9_.-]{0,63}$")
_CAUSAL_FOLLOW_UPS = frozenset(("sao vậy", "tại sao", "vì sao", "sao không được", "why", "why not"))
_FAILURES = {
    "capability_unavailable": "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này.",
    "agent_unavailable": "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này.",
    "scope_not_granted": "Phiên thao tác này chưa được cấp quyền điều khiển âm lượng.",
    "invalid_arguments": "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows.",
    "unsupported_platform": "Điều khiển âm lượng hiện chỉ hỗ trợ Windows.",
    "default_endpoint_unavailable": "Windows chưa có thiết bị phát mặc định mà em có thể truy cập.",
    "read_failed": "Em không đọc được trạng thái âm lượng hiện tại.",
    "write_failed": "Windows không chấp nhận thay đổi âm lượng.",
    "readback_mismatch": "Windows không xác nhận mức âm lượng sau khi thay đổi.",
    "adapter_error": "Bộ điều khiển âm lượng gặp lỗi nên em không coi thao tác là thành công.",
    "receipt_unverified": "Kết quả thay đổi chưa được xác minh nên em không coi thao tác là thành công.",
    "receipt_mismatch": "Kết quả trả về không khớp với thao tác vừa yêu cầu.",
    "invalid_receipt": "Kết quả thao tác không hợp lệ nên em không coi đó là thành công.",
    "cancelled": "Thao tác âm lượng đã bị huỷ.",
    "timed_out": "Thao tác âm lượng đã hết thời gian chờ.",
    "receipt_late": "Thao tác âm lượng đã hết thời gian chờ.",
}
_FALLBACK = "Thao tác âm lượng thất bại và em không có lý do an toàn cụ thể hơn."


class ActionOutcomeError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class ActionOutcome:
    schema_version: int
    tool_id: str
    tool_version: str
    status: ActionOutcomeStatus
    reason_code: str | None

    def to_payload(self) -> dict[str, object]:
        return {
            "schema_version": self.schema_version,
            "tool_id": self.tool_id,
            "tool_version": self.tool_version,
            "status": self.status,
            "reason_code": self.reason_code,
        }


@dataclass(frozen=True, slots=True)
class VolumeReadback:
    operation: str
    level: int
    muted: bool


def parse_action_outcome(value: object) -> ActionOutcome:
    if not isinstance(value, Mapping) or set(value) != _FIELDS:
        raise ActionOutcomeError("invalid_action_outcome")
    schema_version = value.get("schema_version")
    tool_id = value.get("tool_id")
    tool_version = value.get("tool_version")
    status = value.get("status")
    reason_code = value.get("reason_code")
    if (
        schema_version != 1
        or isinstance(schema_version, bool)
        or tool_id != "system.volume"
        or tool_version != "1.0.0"
        or status not in _STATUSES
        or (status == "succeeded" and reason_code is not None)
        or (status != "succeeded" and (not isinstance(reason_code, str) or _SAFE_CODE.fullmatch(reason_code) is None))
    ):
        raise ActionOutcomeError("invalid_action_outcome")
    return ActionOutcome(1, tool_id, tool_version, status, reason_code)


def parse_optional_action_outcome(value: object) -> ActionOutcome | None:
    return None if value is None else parse_action_outcome(value)


def parse_volume_readback(expected_operation: str, value: object) -> VolumeReadback:
    if not isinstance(value, Mapping) or set(value) != {"operation", "level", "muted"}:
        raise ActionOutcomeError("invalid_volume_readback")
    operation = value.get("operation")
    level = value.get("level")
    muted = value.get("muted")
    if (
        operation != expected_operation
        or operation not in {"get", "set", "increase", "decrease", "mute", "unmute"}
        or isinstance(level, bool)
        or not isinstance(level, int)
        or not 0 <= level <= 100
        or not isinstance(muted, bool)
    ):
        raise ActionOutcomeError("invalid_volume_readback")
    return VolumeReadback(operation, level, muted)


def is_causal_follow_up(prompt: str) -> bool:
    if not isinstance(prompt, str) or len(prompt) > 64:
        return False
    normalized = " ".join(unicodedata.normalize("NFKC", prompt).casefold().split()).rstrip("?!.")
    return normalized in _CAUSAL_FOLLOW_UPS


def render_volume_success(readback: VolumeReadback) -> str:
    if readback.operation == "get":
        state = "đang tắt tiếng" if readback.muted else "đang bật tiếng"
        return f"Âm lượng hiện tại là {readback.level}% và {state}, anh."
    if readback.operation == "set":
        return f"Em đã đặt âm lượng về {readback.level}% rồi anh."
    if readback.operation == "increase":
        return f"Em đã tăng âm lượng lên {readback.level}% rồi anh."
    if readback.operation == "decrease":
        return f"Em đã giảm âm lượng xuống {readback.level}% rồi anh."
    if readback.operation == "mute":
        return "Em đã tắt tiếng máy rồi anh."
    return "Em đã bật lại tiếng máy rồi anh."


def render_volume_clarification(reason_code: str) -> str:
    if reason_code == "volume_value_out_of_range":
        return "Mức âm lượng phải từ 0 đến 100; mức tăng hoặc giảm phải từ 1 đến 50."
    if reason_code == "volume_capability_question":
        return "Em có thể dùng công cụ âm lượng khi tính năng này sẵn sàng. Anh muốn đặt, tăng, giảm, tắt tiếng hay bật tiếng?"
    return "Anh muốn đặt âm lượng về bao nhiêu phần trăm, hay tăng hoặc giảm bao nhiêu?"


def render_volume_failure(reason_code: str | None) -> str:
    return _FAILURES.get(reason_code or "", _FALLBACK)


def render_action_explanation(outcome: ActionOutcome) -> str:
    reason = render_volume_clarification(outcome.reason_code or "") if outcome.status == "clarification_required" else render_volume_failure(outcome.reason_code)
    if reason.startswith("Windows"):
        return f"Vì {reason}"
    return f"Vì {reason[0].lower()}{reason[1:]}"
```

Keep `ActionOutcome` free of request IDs, correlation IDs, arguments, output, prompt, visible reply, endpoint proof, and exceptions. Add tests for unknown status, over-64-character reason, non-string reason, success with a reason, failure without a reason, extra fields, and malformed readback.

- [ ] **Step 4: Run outcome tests and verify green**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_action_outcome.py
```

Expected: all tests pass; unknown reason codes render the conservative fallback.

- [ ] **Step 5: Commit the bounded outcome slice**

```powershell
git add -- src/ru_runtime/worker/v1/action_outcome.py tests/test_worker_action_outcome.py
git commit -m "feat: add bounded action outcome rendering"
```

---

### Task 3: Rewire the Worker to Deterministic-First Volume Execution

**Files:**
- Modify: `src/ru_runtime/worker/v1/action_loop.py`
- Modify: `src/ru_runtime/worker/v1/handlers.py`
- Modify: `src/ru_runtime/persistence/diagnostic_schema.py`
- Modify: `tests/test_worker_action_loop.py`
- Modify: `tests/test_worker_runtime_handlers.py`
- Modify: `tests/test_diagnostic_schema.py`
- Modify: `tests/test_privacy_logging.py`

- [ ] **Step 1: Replace model-selection expectations with failing deterministic integration tests**

Change the current volume tests so both modes require zero model calls before and after execution:

```python
@pytest.mark.parametrize("agent_mode", (False, True))
def test_explicit_volume_command_emits_one_request_without_model_generation(agent_mode: bool) -> None:
    runner = SequencedRunner("model must not be called")

    replies = asyncio.run(_run_action_turn(runner, _verified_result(), agent_mode))

    request = [reply for reply in replies if reply.type == CHAT_TOOL_REQUEST]
    assert len(request) == 1
    assert request[0].payload["arguments"] == {"operation": "set", "level": 2}
    assert runner.calls == []
    assert replies[-1].payload == {
        "raw_reply": "Em đã đặt âm lượng về 2% rồi anh.",
        "action_outcome": {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "succeeded",
            "reason_code": None,
        },
    }


def test_ordinary_agent_chat_uses_normal_streaming_generation() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Em đang nghe anh đây.")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, True, prompt="Hôm nay em thế nào?"),
        asyncio.Event(),
    )))

    assert len(runner.calls) == 1
    assert runner.calls[0]["structured_tools_only"] is False
    assert runner.calls[0]["agent_follow_up"] is False
    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert [reply.type for reply in replies] == [CHAT_RESET, CHAT_CHUNK, CHAT_DONE]


@pytest.mark.parametrize(
    ("error_code", "expected_reply"),
    (
        ("default_endpoint_unavailable", "Windows chưa có thiết bị phát mặc định mà em có thể truy cập."),
        ("write_failed", "Windows không chấp nhận thay đổi âm lượng."),
        ("readback_mismatch", "Windows không xác nhận mức âm lượng sau khi thay đổi."),
    ),
)
def test_failed_receipt_has_typed_reason_and_no_model_continuation(error_code: str, expected_reply: str) -> None:
    runner = SequencedRunner("model must not be called")
    result = _verified_result()
    result.update(status="failed", error_code=error_code, verification_state="failed")

    replies = asyncio.run(_run_action_turn(runner, result, True))

    assert runner.calls == []
    assert replies[-1].payload["raw_reply"] == expected_reply
    assert replies[-1].payload["action_outcome"]["reason_code"] == error_code
```

Add cases proving ambiguity and out-of-range values emit no tool request and no model call, capability absence maps to `capability_unavailable`, a malformed/unverified/mismatched receipt cannot produce success, immediate causal follow-up with `last_action_outcome` bypasses the model, and unrelated follow-up calls the model normally.

Use these concrete Worker assertions for the pre-dispatch and continuity branches:

```python
@pytest.mark.parametrize(
    ("prompt", "reason_code"),
    (
        ("Giảm âm lượng 2%", "volume_command_ambiguous"),
        ("Đặt âm lượng về 101", "volume_value_out_of_range"),
    ),
)
def test_unclear_volume_command_stops_before_model_and_tool(
    prompt: str,
    reason_code: str,
) -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("model must not be called")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, False, prompt=prompt),
        asyncio.Event(),
    )))

    assert runner.calls == []
    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert replies[-1].payload["action_outcome"]["reason_code"] == reason_code


def test_immediate_causal_follow_up_uses_bounded_outcome_without_model() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("model must not be called")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
    payload = _action_payload(queue, False, prompt="sao vậy?")
    payload["last_action_outcome"] = {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "write_failed",
    }

    replies = asyncio.run(collect(handlers.chat(payload, asyncio.Event())))

    assert runner.calls == []
    assert replies[-1].payload == {
        "raw_reply": "Vì Windows không chấp nhận thay đổi âm lượng."
    }


def test_unrelated_follow_up_ignores_bounded_outcome_and_uses_normal_model() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Em nghe đây.")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
    payload = _action_payload(queue, True, prompt="Kể em nghe chuyện khác")
    payload["last_action_outcome"] = {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "write_failed",
    }

    replies = asyncio.run(collect(handlers.chat(payload, asyncio.Event())))

    assert len(runner.calls) == 1
    assert runner.calls[0]["structured_tools_only"] is False
    assert replies[-1].payload == {"raw_reply": "Em nghe đây."}
```

Delete the obsolete handler-level tests that feed model-authored JSON, arrays of model tool calls, or legacy volume markers into the execution path. Preserve the direct `validate_selection` and `parse_legacy_volume_intent` unit tests because those compatibility helpers remain non-executable library behavior.

- [ ] **Step 2: Run the Worker integration tests and verify red**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_runtime_handlers.py tests/test_worker_action_loop.py -k "volume or receipt or agent_chat or causal"
```

Expected: failures show the current two model calls, generic failure text, and missing `action_outcome`.

- [ ] **Step 3: Retain only strictly validated volume readback on successful receipts**

In `action_loop.py`, import `field`, `VolumeReadback`, `ActionOutcomeError`, and `parse_volume_readback`. Change `ActionReceipt` to:

```python
@dataclass(frozen=True, slots=True)
class ActionReceipt:
    status: str
    error_code: str | None
    safe_summary: str
    duration_ms: int
    verification_state: str
    readback: VolumeReadback | None = field(default=None, repr=False)
```

Immediately before returning the receipt, parse output only for a succeeded receipt:

```python
    readback = None
    if status == "succeeded":
        try:
            readback = parse_volume_readback(str(selection.arguments["operation"]), output)
        except ActionOutcomeError:
            raise ActionProtocolError("invalid_receipt") from None

    return ActionReceipt(
        status=status,
        error_code=error_code,
        safe_summary=safe_summary.strip(),
        duration_ms=duration_ms,
        verification_state=verification_state,
        readback=readback,
    )
```

Remove `detect_volume_intent` and its broad keyword constants from `action_loop.py`. Keep `validate_selection` and the legacy parser as non-executable compatibility helpers for future provider-native structured selection; no Worker production path may call them for volume.

- [ ] **Step 4: Implement deterministic orchestration and one-shot explanation in the Worker**

Parse `last_action_outcome` as an optional top-level chat field in `_parse_chat_request` and add it to `_ChatRequest`. At the start of `run_generation`, use this exact branch order:

```python
            if request.last_action_outcome is not None and is_causal_follow_up(request.prompt):
                publish(WorkerReply(
                    CHAT_DONE,
                    {"raw_reply": render_action_explanation(request.last_action_outcome)},
                ))
                return

            decision = resolve_volume_intent(request.prompt)
            if decision.kind == "clarification_required":
                event_code = (
                    "tool.intent.rejected"
                    if decision.reason_code == "volume_value_out_of_range"
                    else "tool.intent.clarification"
                )
                _record_intent(
                    event_code,
                    correlation_id=correlation_id,
                    status="failed" if event_code.endswith("rejected") else "skipped",
                    error_code=decision.reason_code,
                )
                outcome = ActionOutcome(
                    1,
                    "system.volume",
                    "1.0.0",
                    "clarification_required",
                    decision.reason_code,
                )
                publish(WorkerReply(CHAT_DONE, {
                    "raw_reply": render_volume_clarification(decision.reason_code or "volume_command_ambiguous"),
                    "action_outcome": outcome.to_payload(),
                }))
                return

            if decision.kind == "execute":
                call = decision.tool_call
                assert call is not None
                capability = next(
                    (
                        item for item in request.model_tools
                        if item.tool_id == call.tool_id and item.tool_version == call.tool_version
                    ),
                    None,
                )
                if capability is None or not isinstance(tool_result_queue, asyncio.Queue):
                    reason_code = "capability_unavailable" if capability is None else "agent_unavailable"
                    _record_intent(
                        "tool.intent.rejected",
                        correlation_id=correlation_id,
                        status="failed",
                        error_code=reason_code,
                    )
                    outcome = ActionOutcome(1, call.tool_id, call.tool_version, "failed", reason_code)
                    publish(WorkerReply(CHAT_DONE, {
                        "raw_reply": render_volume_failure(reason_code),
                        "action_outcome": outcome.to_payload(),
                    }))
                    return

                selection = ActionSelection(
                    call.tool_id,
                    call.tool_version,
                    capability.capability_class,
                    dict(call.arguments),
                )
                _record_intent(
                    "tool.intent.resolved",
                    correlation_id=correlation_id,
                    status="succeeded",
                    error_code=None,
                )
                tool_request_id = f"tool-{uuid.uuid4().hex}"
                tool_correlation_id = correlation_id or f"corr-{uuid.uuid4().hex}"
                deadline_at = time.monotonic() + (_ACTION_DEADLINE_MS / 1000.0)
                publish(WorkerReply(CHAT_TOOL_REQUEST, {
                    "request_id": tool_request_id,
                    "session_id": session_id,
                    "correlation_id": tool_correlation_id,
                    "tool_id": selection.tool_id,
                    "tool_version": selection.tool_version,
                    "capability_class": selection.capability_class,
                    "arguments": dict(selection.arguments),
                    "deadline_ms": _ACTION_DEADLINE_MS,
                }))
                try:
                    result = await wait_for_tool_result(deadline_at)
                    receipt = validate_receipt(
                        selection,
                        result,
                        expected_request_id=tool_request_id,
                        expected_session_id=session_id,
                        expected_correlation_id=tool_correlation_id,
                        deadline_at=deadline_at,
                    )
                except ActionProtocolError as error:
                    _record_receipt_rejection(
                        error,
                        correlation_id=tool_correlation_id,
                        tool_id=selection.tool_id,
                        tool_version=selection.tool_version,
                    )
                    outcome = ActionOutcome(1, selection.tool_id, selection.tool_version, "failed", error.code)
                    publish(WorkerReply(CHAT_DONE, {
                        "raw_reply": render_volume_failure(error.code),
                        "action_outcome": outcome.to_payload(),
                    }))
                    return

                if receipt.status != "succeeded" or receipt.readback is None:
                    reason_code = receipt.error_code or receipt.status
                    outcome = ActionOutcome(1, selection.tool_id, selection.tool_version, receipt.status, reason_code)
                    publish(WorkerReply(CHAT_DONE, {
                        "raw_reply": render_volume_failure(reason_code),
                        "action_outcome": outcome.to_payload(),
                    }))
                    return

                outcome = ActionOutcome(1, selection.tool_id, selection.tool_version, "succeeded", None)
                publish(WorkerReply(CHAT_DONE, {
                    "raw_reply": render_volume_success(receipt.readback),
                    "action_outcome": outcome.to_payload(),
                }))
                return

            raw_reply = await asyncio.to_thread(
                generate_once,
                request.prompt,
                request.turns,
                stream=True,
                observe_user=True,
                phase="normal",
            )
            if not isinstance(raw_reply, str):
                raise TypeError("model reply must be text")
            publish(WorkerReply(CHAT_DONE, {"raw_reply": raw_reply}))
```

Delete the old `selection_required`, tool-result model continuation, and generic `failure_reply` execution branches. Keep the existing outer `generation_failed` protection for unexpected non-action exceptions.

- [ ] **Step 5: Add privacy-safe intent diagnostics**

Add exactly these values to `ALLOWED_EVENT_CODES`:

```python
"tool.intent.resolved",
"tool.intent.clarification",
"tool.intent.rejected",
```

Add `_record_intent` beside `_record_receipt_rejection`; it may set only event ID/time, `worker.runtime`, fixed tool ID/version, status, stable error code, and an already available valid correlation ID. Tests must serialize the event and assert the absence of `prompt`, `arguments`, `operation`, `level`, `amount`, `output`, `muted`, `reply`, `device`, and `exception`.

Use this exact helper shape so parser captures cannot be passed accidentally:

```python
def _record_intent(
    event_code: str,
    *,
    correlation_id: str,
    status: str,
    error_code: str | None,
) -> None:
    try:
        get_diagnostic_log_writer().try_enqueue(
            DiagnosticEvent(
                event_id=f"evt_{uuid.uuid4().hex}",
                ts=time.time(),
                iso=datetime.now(timezone.utc).isoformat(),
                level="warning" if status == "failed" else "info",
                component="worker.runtime",
                event_code=event_code,
                correlation_id=correlation_id or None,
                status=status,
                error_code=error_code,
                tool_id="system.volume",
                tool_version="1.0.0",
            )
        )
    except Exception:
        return
```

- [ ] **Step 6: Run focused Worker and privacy tests and verify green**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_intent.py tests/test_worker_action_outcome.py tests/test_worker_action_loop.py tests/test_worker_runtime_handlers.py tests/test_diagnostic_schema.py tests/test_privacy_logging.py
```

Expected: all selected tests pass; an explicit command emits exactly one request with zero model calls in both modes.

- [ ] **Step 7: Commit the Worker execution slice**

```powershell
git add -- src/ru_runtime/worker/v1/action_loop.py src/ru_runtime/worker/v1/handlers.py src/ru_runtime/persistence/diagnostic_schema.py tests/test_worker_action_loop.py tests/test_worker_runtime_handlers.py tests/test_diagnostic_schema.py tests/test_privacy_logging.py
git commit -m "fix: execute volume intents without model selection"
```

---

### Task 4: Extend the Typed Worker Protocol Across Python and C#

**Files:**
- Modify: `src/ru_runtime/worker/v1/protocol.py`
- Modify: `tests/test_worker_protocol.py`
- Create: `backend-dotnet/RuBackend/Worker/WorkerActionOutcome.cs`
- Modify: `backend-dotnet/RuBackend/Worker/WorkerModels.cs`
- Modify: `backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs`
- Modify: `backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs`
- Create: `backend-dotnet/RuBackend.Tests/Worker/WorkerActionOutcomeTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs`

- [ ] **Step 1: Write failing Python and C# protocol tests**

Python must accept `{raw_reply, action_outcome}` and reject extra outcome fields, missing reason for failure, reason over 64 characters, success with a reason, unknown status, and malformed tool binding. C# must assert the same cases and prove `BuildChatPayload` emits no `last_action_outcome` property when absent and this exact property when present:

```json
{
  "schema_version": 1,
  "tool_id": "system.volume",
  "tool_version": "1.0.0",
  "status": "failed",
  "reason_code": "write_failed"
}
```

Use a round-trip test with:

```csharp
var outcome = WorkerActionOutcome.Parse(new JsonObject
{
    ["schema_version"] = 1,
    ["tool_id"] = "system.volume",
    ["tool_version"] = "1.0.0",
    ["status"] = "failed",
    ["reason_code"] = "write_failed",
});

Assert.True(outcome.ShouldRetain);
Assert.Equal("write_failed", outcome.ToJson()["reason_code"]!.GetValue<string>());
```

- [ ] **Step 2: Run both focused protocol suites and verify red**

Run sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_protocol.py
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WorkerActionOutcomeTests|FullyQualifiedName~WorkerEnvelopeTests|FullyQualifiedName~LoopbackWorkerClientTests"
```

Expected: Python accepts malformed `chat.done` today, and C# has no `WorkerActionOutcome` type or outbound field.

- [ ] **Step 3: Implement one strict C# codec and use it at both wire directions**

Create `WorkerActionOutcome.cs` with this public contract:

```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RuBackend.Worker;

public sealed record WorkerActionOutcome(
    int SchemaVersion,
    string ToolId,
    string ToolVersion,
    string Status,
    string? ReasonCode)
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "succeeded", "failed", "denied", "approval_required",
        "cancelled", "timed_out", "clarification_required",
    };
    private static readonly Regex SafeCode = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema_version", "tool_id", "tool_version", "status", "reason_code",
    };

    public bool ShouldRetain => !string.Equals(Status, "succeeded", StringComparison.Ordinal);

    public JsonObject ToJson() => new()
    {
        ["schema_version"] = SchemaVersion,
        ["tool_id"] = ToolId,
        ["tool_version"] = ToolVersion,
        ["status"] = Status,
        ["reason_code"] = ReasonCode,
    };

    public static WorkerActionOutcome? ParseOptional(JsonNode? node) =>
        node is null ? null : Parse(node);

    public static WorkerActionOutcome Parse(JsonNode node)
    {
        if (node is not JsonObject root
            || root.Count != Fields.Count
            || root.Any(pair => !Fields.Contains(pair.Key))
            || root["schema_version"]?.GetValue<int>() != 1
            || root["tool_id"]?.GetValue<string>() != "system.volume"
            || root["tool_version"]?.GetValue<string>() != "1.0.0")
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        var status = root["status"]?.GetValue<string>();
        var reason = root["reason_code"]?.GetValue<string>();
        if (status is null
            || !AllowedStatuses.Contains(status)
            || status == "succeeded" && reason is not null
            || status != "succeeded" && (reason is null || !SafeCode.IsMatch(reason)))
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        return new WorkerActionOutcome(1, "system.volume", "1.0.0", status, reason);
    }
}
```

Catch `InvalidOperationException`, `FormatException`, and `OverflowException` inside `Parse` and rethrow only `WorkerProtocolException("invalid_action_outcome")`, so malformed JSON never escapes as a framework error.

Add this non-positional property to `WorkerChatRequest`:

```csharp
public WorkerActionOutcome? LastActionOutcome { get; init; }
```

In `BuildChatPayload`, replace the direct return with:

```csharp
var payload = new JsonObject
{
    ["prompt"] = request.Prompt,
    ["session_id"] = request.SessionId,
    ["turns"] = turns,
    ["memory_context"] = request.MemoryContext,
    ["vts_instruction"] = request.VtsInstruction,
    ["settings"] = request.Settings.DeepClone(),
};
if (request.LastActionOutcome is not null)
{
    payload["last_action_outcome"] = request.LastActionOutcome.ToJson();
}
return payload;
```

In `WorkerEnvelopeCodec.Validate`, validate `ChatDone` with exact payload keys `raw_reply` plus optional `action_outcome`, a non-empty `raw_reply` no longer than 32,000 characters, and `WorkerActionOutcome.Parse` for the optional object.

- [ ] **Step 4: Add matching Python `chat.done` validation**

In `validate_envelope`, add:

```python
    elif message_type == CHAT_DONE:
        _validate_chat_done_payload(payload)
```

The helper must require only `raw_reply` and optional `action_outcome`, bound `raw_reply` to 32,000 characters, and call `parse_action_outcome`; translate `ActionOutcomeError` to `ProtocolError("invalid_action_outcome")`.

```python
def _validate_chat_done_payload(payload: Mapping[str, Any]) -> None:
    if set(payload) not in ({"raw_reply"}, {"raw_reply", "action_outcome"}):
        raise ProtocolError("invalid_chat_done")
    raw_reply = payload.get("raw_reply")
    if not isinstance(raw_reply, str) or not raw_reply.strip() or len(raw_reply) > 32_000:
        raise ProtocolError("invalid_chat_done")
    if "action_outcome" in payload:
        try:
            parse_action_outcome(payload["action_outcome"])
        except ActionOutcomeError:
            raise ProtocolError("invalid_action_outcome") from None
```

- [ ] **Step 5: Run focused protocol suites and verify green**

Run sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_protocol.py tests/test_worker_server.py
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WorkerActionOutcomeTests|FullyQualifiedName~WorkerEnvelopeTests|FullyQualifiedName~LoopbackWorkerClientTests"
```

Expected: all focused Python and C# protocol tests pass and malformed outcome metadata fails closed at both boundaries.

- [ ] **Step 6: Commit the cross-language protocol slice**

```powershell
git add -- src/ru_runtime/worker/v1/protocol.py tests/test_worker_protocol.py backend-dotnet/RuBackend/Worker/WorkerActionOutcome.cs backend-dotnet/RuBackend/Worker/WorkerModels.cs backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs backend-dotnet/RuBackend.Tests/Worker/WorkerActionOutcomeTests.cs backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs
git commit -m "feat: carry bounded action outcomes across worker protocol"
```

---

### Task 5: Add One-Shot Session Outcome Continuity in the Host

**Files:**
- Create: `backend-dotnet/RuBackend/Conversation/ActionOutcomeContextStore.cs`
- Modify: `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Create: `backend-dotnet/RuBackend.Tests/Conversation/ActionOutcomeContextStoreTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs`

- [ ] **Step 1: Write failing store and coordinator tests**

Cover replace, atomic take, matching-session isolation, and removal:

```csharp
[Fact]
public void Put_replaces_and_take_is_one_shot_per_session()
{
    IActionOutcomeContextStore store = new ActionOutcomeContextStore();
    store.Put("session-1", Failed("write_failed"));
    store.Put("session-1", Failed("readback_mismatch"));
    store.Put("session-2", Failed("read_failed"));

    Assert.Equal("readback_mismatch", store.Take("session-1")!.ReasonCode);
    Assert.Null(store.Take("session-1"));
    Assert.Equal("read_failed", store.Take("session-2")!.ReasonCode);
}
```

Add coordinator tests proving:

1. A failed `chat.done.action_outcome` is retained after the visible reply is appended.
2. The next same-session Worker request receives `LastActionOutcome` exactly once.
3. An unrelated next turn also consumes it, so a third turn receives null.
4. A succeeded outcome is not retained.
5. Session messages and memory calls contain only the visible prompt/reply and never the structured outcome JSON or reason field name.

- [ ] **Step 2: Run focused Host tests and verify red**

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ActionOutcomeContextStoreTests|FullyQualifiedName~ConversationCoordinatorTests"
```

Expected: the store type and `LastActionOutcome` flow are missing.

- [ ] **Step 3: Implement the small thread-safe one-shot store**

Create:

```csharp
using System.Collections.Concurrent;
using RuBackend.Worker;

namespace RuBackend.Conversation;

public interface IActionOutcomeContextStore
{
    void Put(string sessionId, WorkerActionOutcome outcome);
    WorkerActionOutcome? Take(string sessionId);
    void Remove(string sessionId);
}

public sealed class ActionOutcomeContextStore : IActionOutcomeContextStore
{
    private readonly ConcurrentDictionary<string, WorkerActionOutcome> _outcomes =
        new(StringComparer.Ordinal);

    public void Put(string sessionId, WorkerActionOutcome outcome)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.ShouldRetain)
        {
            _outcomes.TryRemove(sessionId, out _);
            return;
        }
        _outcomes[sessionId] = outcome;
    }

    public WorkerActionOutcome? Take(string sessionId)
    {
        ValidateSessionId(sessionId);
        return _outcomes.TryRemove(sessionId, out var outcome) ? outcome : null;
    }

    public void Remove(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _outcomes.TryRemove(sessionId, out _);
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }
    }
}
```

Register it before `IConversationCoordinator`:

```csharp
builder.Services.AddSingleton<IActionOutcomeContextStore, ActionOutcomeContextStore>();
```

- [ ] **Step 4: Wire consume-before-turn and retain-after-done**

Inject `IActionOutcomeContextStore` into `ConversationCoordinator`. Immediately after session resolution, call `Take(sessionId)` once and set the result on `WorkerChatRequest.LastActionOutcome`. In the `ChatDone` case, parse the optional outcome before memory or session persistence, but retain it only after the visible assistant message has been appended successfully:

Store `actionOutcome` immediately after reading `raw_reply`:

```csharp
var actionOutcome = WorkerActionOutcome.ParseOptional(item.Payload["action_outcome"]);
```

After the existing `AppendAsync(sessionId, [new SessionMessage("ai", fullReply)], cancellationToken)` returns `true`, run:

```csharp
if (actionOutcome is { ShouldRetain: true })
{
    _actionOutcomes.Put(sessionId, actionOutcome);
}
else if (actionOutcome is not null)
{
    _actionOutcomes.Remove(sessionId);
}
```

Do not add outcome data to `SessionMessage`, `TryRememberAsync`, `TryEnqueueLearning`, Activity Timeline, VTS, TTS, or diagnostic calls.

- [ ] **Step 5: Run focused Host tests and verify green**

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ActionOutcomeContextStoreTests|FullyQualifiedName~ConversationCoordinatorTests"
```

Expected: all selected tests pass; the previous outcome appears on exactly one next Worker request and nowhere persistent.

- [ ] **Step 6: Commit the Host continuity slice**

```powershell
git add -- backend-dotnet/RuBackend/Conversation/ActionOutcomeContextStore.cs backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/Conversation/ActionOutcomeContextStoreTests.cs backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs
git commit -m "feat: preserve one bounded action failure context"
```

---

### Task 6: Clear One-Shot Context on Session Lifecycle Boundaries

**Files:**
- Modify: `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs`
- Modify: `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`
- Modify: `backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`

- [ ] **Step 1: Write failing switch, disconnect, and deletion tests**

Use the actual singleton store in endpoint tests. Seed `session-1`, send a turn for `session-2`, and assert `Take("session-1")` returns null. Seed the active session, close the WebSocket, wait for endpoint cleanup, and assert the same. For the business endpoint, create a session, seed its outcome, delete it successfully, and assert the context is gone.

The disconnect assertion must use a bounded polling helper with a two-second deadline; it must not use a fixed multi-second sleep.

- [ ] **Step 2: Run lifecycle tests and verify red**

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ChatWebSocketEndpointTests|FullyQualifiedName~BusinessEndpointTests"
```

Expected: seeded outcomes survive switch, disconnect, and deletion.

- [ ] **Step 3: Track the active session inside one WebSocket connection**

Inject `IActionOutcomeContextStore` into `ChatWebSocketEndpoint.HandleAsync`. Maintain one local `activeSessionId` and use this exact transition rule:

```csharp
void ObserveSession(string? candidate)
{
    if (string.IsNullOrWhiteSpace(candidate))
    {
        return;
    }
    if (activeSessionId is not null
        && !string.Equals(activeSessionId, candidate, StringComparison.Ordinal))
    {
        actionOutcomes.Remove(activeSessionId);
    }
    activeSessionId = candidate;
}
```

Call it for the requested session before `StreamTurnAsync` and for every emitted `ChatEvent.SessionId` inside `StreamTurnAsync`. In `HandleAsync`'s `finally`, remove the final active session. This makes switch and disconnect cleanup deterministic without persisting connection state.

- [ ] **Step 4: Clear context only after successful session deletion**

Inject `IActionOutcomeContextStore` into `DeleteSessionAsync` and use:

```csharp
var deleted = await sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
if (deleted)
{
    actionOutcomes.Remove(sessionId);
}
```

Do not remove context when deletion reports that the session does not exist or throws.

- [ ] **Step 5: Run lifecycle tests and verify green**

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ChatWebSocketEndpointTests|FullyQualifiedName~BusinessEndpointTests|FullyQualifiedName~ActionOutcomeContextStoreTests"
```

Expected: all selected tests pass, including bounded disconnect cleanup.

- [ ] **Step 6: Commit the lifecycle slice**

```powershell
git add -- backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs backend-dotnet/RuBackend/Business/BusinessEndpoints.cs backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs
git commit -m "fix: clear action context at session boundaries"
```

---

### Task 7: Make WinUI Agent Activity Typed and Truthful

**Files:**
- Create: `gui-winui3/Services/AgentActivityModel.cs`
- Delete: `gui-winui3/Services/AgentActivityItem.cs`
- Modify: `gui-winui3/Services/ChatWebSocketClient.cs`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Create: `gui-winui3.Tests/AgentActivityModelTests.cs`
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`
- Modify: `gui-winui3.Tests/AgentSessionContractTests.cs`
- Modify: `gui-winui3.Harness/UiAutomationScenario.cs`

- [ ] **Step 1: Write failing typed-frame and source-contract tests**

Test these exact display states:

```csharp
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
```

Reject wrong type, blank/oversized session or tool ID, unknown status, unsafe/oversized error code, duplicate or unexpected properties, and raw private properties such as `arguments`, `output`, `level`, `device_name`, and `exception`.

Update the source contract to require `Agent mode đã bật`, `Đang thực hiện thao tác đã cấp quyền…`, and typed `AgentActivityModel`; ban the old Host-waiting text, old raw-scope grant text, and assignment from raw `content`. Build the two obsolete literals from fragments so repository scans can prove they are absent from current production and test sources:

```csharp
var oldWaitingCopy = string.Concat("Đang chờ ", "Host cấp quyền…");
var oldGrantCopy = string.Concat("Quyền đã ", "cấp:");
Assert.DoesNotContain(oldWaitingCopy, codeBehind, StringComparison.Ordinal);
Assert.DoesNotContain(oldGrantCopy, codeBehind, StringComparison.Ordinal);
```

- [ ] **Step 2: Run focused GUI tests and verify red**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentActivityModelTests|FullyQualifiedName~AgentSessionUiSourceContractTests|FullyQualifiedName~AgentSessionContractTests"
```

Expected: the typed model is missing and the old Host-waiting/grant copy is still present.

- [ ] **Step 3: Implement strict Agent activity parsing and copy**

Create `AgentActivityModel` with required `type`, `session_id`, `status`, and `tool_id`; optional `error_code`; maximum session/tool length 128 and safe-code length 64. Accept only the terminal statuses already emitted by `AgentTurnCoordinator` plus `requested`. Never expose a raw `content` field as display text.

Use one exhaustive `DisplayText` switch and the same stable reason meanings as the Worker. Unknown safe codes use:

```text
Thao tác thất bại: Không có lý do an toàn cụ thể hơn.
```

Implement the model with this exact public surface:

```csharp
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
```

Delete the unused `AgentActivityItem.cs` after `rg -n "AgentActivityItem" gui-winui3 gui-winui3.Tests` confirms it has no consumers.

- [ ] **Step 4: Wire typed events and honest state transitions**

Change the event to:

```csharp
public event Action<AgentActivityModel>? OnAgentActivityReceived;
```

For `agent.activity`, call `AgentActivityModel.TryParse` and ignore malformed frames. In `ChatView.xaml.cs`:

```csharp
// Toggle enabled, no request exists.
AgentActivity.Text = "Agent mode đã bật";

// Actual grant received for the active session.
AgentActivity.Text = "Đang thực hiện thao tác đã cấp quyền…";

// Typed activity for the active session.
AgentActivity.Text = activity.DisplayText;
```

The WebSocket branch is:

```csharp
else if (msgType == "agent.activity"
    && AgentActivityModel.TryParse(root, out var activity))
{
    OnAgentActivityReceived?.Invoke(activity);
}
```

Keep stale-session rejection. On toggle off, session replacement, and disconnect, collapse the label and clear pending approval state. Update `OpenSessionAsync` to call `_agentSession.ObserveSession(sessionId)`, disable Agent mode, uncheck the toggle, and clear activity so a manual history switch follows the same rule as a WebSocket-created session.

Change the Harness enable check from the obsolete word `bounded` to exact containment of `Agent mode đã bật`.

- [ ] **Step 5: Run GUI unit and source-contract tests and verify green**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentActivityModelTests|FullyQualifiedName~AgentSessionUiSourceContractTests|FullyQualifiedName~AgentSessionContractTests|FullyQualifiedName~AgentApproval"
```

Expected: all selected GUI tests pass; no UI path displays raw Agent event content.

- [ ] **Step 6: Commit the truthful Agent UI slice**

```powershell
git add -- gui-winui3/Services/AgentActivityModel.cs gui-winui3/Services/AgentActivityItem.cs gui-winui3/Services/ChatWebSocketClient.cs gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentActivityModelTests.cs gui-winui3.Tests/AgentSessionUiSourceContractTests.cs gui-winui3.Tests/AgentSessionContractTests.cs gui-winui3.Harness/UiAutomationScenario.cs
git commit -m "fix: show truthful typed agent activity"
```

---

### Task 8: Update the Deterministic Harness, Privacy Gates, and Current Documentation

**Files:**
- Modify: `scripts/harness/deterministic_worker.py`
- Modify: `tests/test_agent_harness.py`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/logging.md`
- Modify: `docs/operations/verification.md`
- Modify only if its current user-facing claims changed: `README.md`

- [ ] **Step 1: Write failing Harness and documentation contract assertions**

Require the deterministic failed Agent fixture to emit this `chat.done` payload:

```python
{
    "raw_reply": "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows.",
    "action_outcome": {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "invalid_arguments",
    },
}
```

Assert the Harness artifact and event sink contain none of `arguments`, `output`, `level`, `amount`, `muted`, `prompt`, `reply`, `device_name`, `endpoint_hash`, or exception text. Extend `tests/test_current_documentation.py` to require deterministic-first volume selection, one-shot outcome continuity, the three intent diagnostic codes, and the manual native-volume disclaimer in current operations docs.

- [ ] **Step 2: Run Harness/documentation tests and verify red**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_agent_harness.py tests/test_harness_session.py tests/test_current_documentation.py
```

Expected: failed fixture lacks `action_outcome` and current docs still describe prompt-only model selection or the old UI state.

- [ ] **Step 3: Update the synthetic Worker fixture without native access**

Change only the failed `CHAT_DONE` fixture to the payload above. Keep its deliberately invalid `level: 101` request at the private synthetic boundary so the Broker fails before Core Audio. Do not add a native acceptance call to the deterministic Harness.

- [ ] **Step 4: Update current operational documentation and remove obsolete current claims**

Document these exact invariants:

- Explicit volume commands use the deterministic parser and never model selection.
- Ordinary normal and Agent-mode chat use normal streamed generation.
- Only verified Broker readback produces success.
- Failed or clarification outcomes are bounded, in-memory, session-isolated, and consumed by the next turn.
- `tool.intent.resolved`, `tool.intent.clarification`, and `tool.intent.rejected` carry no prompt, argument, level, output, device, or exception data.
- Agent idle/requested/granted/terminal copy corresponds to actual events.
- Automated Harness evidence is synthetic and does not claim audible Windows volume acceptance.

Scan current docs and production/tests:

```powershell
rg -n "Đang chờ Host cấp quyền|Quyền đã cấp:|prompt-only volume|model selection call" README.md docs/operations src backend-dotnet/RuBackend gui-winui3 tests backend-dotnet/RuBackend.Tests gui-winui3.Tests
```

Expected: no obsolete current claim remains. Historical accepted specs and completed plans under `docs/superpowers` remain as decision history unless they falsely present themselves as current operations guidance.

- [ ] **Step 5: Run Harness/documentation and privacy tests and verify green**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_agent_harness.py tests/test_harness_session.py tests/test_current_documentation.py tests/test_privacy_logging.py tests/test_memory_extraction.py
```

Expected: all selected tests pass and the structured outcome is absent from persistent memory/history fixtures and artifacts.

- [ ] **Step 6: Commit Harness and documentation**

Stage `README.md` only if Step 4 required a current user-facing change:

```powershell
git add -- scripts/harness/deterministic_worker.py tests/test_agent_harness.py docs/operations/agent-tools.md docs/operations/runtime-behavior.md docs/operations/logging.md docs/operations/verification.md tests/test_current_documentation.py
git commit -m "docs: document deterministic volume recovery"
```

---

### Task 9: Run Sequential Release Gates and Record the Honest Acceptance Boundary

**Files:**
- Modify only when verification reveals an inaccurate current claim: `README.md`, `docs/operations/verification.md`
- Produce ignored artifacts under: `artifacts/gui/`

- [ ] **Step 1: Verify the working tree and diff hygiene**

Run:

```powershell
git status --short --branch
git diff --check
```

Expected: only intended implementation changes exist before their commits; `git diff --check` reports no new whitespace errors.

- [ ] **Step 2: Run the complete Python suite**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
```

Expected: all Python tests pass.

- [ ] **Step 3: Run all .NET suites sequentially**

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
```

Expected: every suite passes with zero failed tests.

- [ ] **Step 4: Verify the public route contract**

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
```

Expected: `41 HTTP + 2 WebSocket` routes match.

- [ ] **Step 5: Publish WinUI and build the GUI Harness**

```powershell
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --nologo
```

Expected: both commands exit 0 with zero build errors.

- [ ] **Step 6: Run deterministic GUI and Agent scenarios with no physical audio mutation**

First close any user-owned RuApp window; the Harness must refuse to take over an existing instance. Then run the standard deterministic GUI suite:

```powershell
& D:\LLM\scripts\run_gui_voice_harness.ps1 -AppPath D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe -DriverProject D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -OutputPath D:\LLM\artifacts\gui\ru-volume-recovery-gui.json
```

Run each Agent scenario sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --project-root D:\LLM --mode gui-deterministic --scenario agent-volume-contract --app-path D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe --driver-project D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --output D:\LLM\artifacts\gui\ru-agent-volume-contract.json
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --project-root D:\LLM --mode gui-deterministic --scenario agent-cancel --app-path D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe --driver-project D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --output D:\LLM\artifacts\gui\ru-agent-cancel.json
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --project-root D:\LLM --mode gui-deterministic --scenario agent-stale-result --app-path D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe --driver-project D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --output D:\LLM\artifacts\gui\ru-agent-stale-result.json
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --project-root D:\LLM --mode gui-deterministic --scenario agent-private-metadata --app-path D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe --driver-project D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --output D:\LLM\artifacts\gui\ru-agent-private-metadata.json
```

Expected: every artifact reports all checks passed, all owned children terminated, all ports closed, and no private metadata. These fixtures do not open the physical Core Audio endpoint.

- [ ] **Step 7: Perform final privacy, documentation, and repository scans**

```powershell
rg -n "Đang chờ Host cấp quyền|Quyền đã cấp:" README.md docs/operations src backend-dotnet/RuBackend gui-winui3 tests backend-dotnet/RuBackend.Tests gui-winui3.Tests
rg -n '"(arguments|output|level|amount|muted|prompt|reply|device_name|exception)"' artifacts/gui/ru-agent-*.json
git status --short --branch
git log --oneline -10
```

Expected: old UI copy is absent; no forbidden private outcome field appears in Agent artifacts; only ignored verification artifacts remain outside Git; commit history consists of the planned focused slices.

- [ ] **Step 8: Commit any verification-driven documentation correction**

If Step 7 found an inaccurate current document and it was corrected, commit only that correction:

```powershell
git add -- README.md docs/operations/verification.md
git commit -m "docs: align volume recovery verification claims"
```

If no current document required correction, do not create an empty commit.

- [ ] **Step 9: Report the acceptance boundary without overclaiming**

Report automated parser/Worker/Host/GUI/Harness evidence as passed only when the commands above actually pass. Report native Windows-volume mutation and the user's audible/visual confirmation as pending; do not run `scripts/native_volume_acceptance.py` unless the user explicitly opts into a real device-changing smoke test.

## Self-Review Record

- Spec coverage: Tasks 1-3 cover deterministic parsing, ambiguity, exact request count, verified receipts, deterministic copy, and diagnostics; Tasks 4-6 cover strict cross-language one-shot continuity and lifecycle cleanup; Task 7 covers honest GUI states; Tasks 8-9 cover privacy, current docs, Harness, and the manual native boundary.
- Placeholder scan: the plan contains concrete file paths, APIs, code, commands, expected failures, and expected passes for every implementation step.
- Type consistency: Python uses `ActionOutcome`, `VolumeReadback`, and `last_action_outcome`; C# uses `WorkerActionOutcome`, `LastActionOutcome`, and `IActionOutcomeContextStore`; both protocols use the same five fields, seven statuses, tool binding, and reason-code bound.
- Maintainability: parsing, outcome rendering, Host storage, and GUI frame rendering remain focused files; existing large handlers/coordinators receive only orchestration calls.
