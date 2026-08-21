from ru_runtime.memory.store import MemoryKind
from ru_runtime.memory.extraction import extract_candidates, is_sensitive_or_unsafe, strip_internal_metadata


def test_extracts_explicit_preference_and_scoped_project_decision():
    candidates = extract_candidates(
        "Anh thích Ru tự động hoá và dự án D:\\LLM sẽ dùng SQLite cho memory.",
        "Em nhớ rồi anh.",
    )

    assert [(item.kind, item.scope) for item in candidates] == [
        (MemoryKind.PREFERENCE, None),
        (MemoryKind.DECISION, "D:\\LLM"),
    ]


def test_rejects_secret_and_vts_control_metadata():
    assert extract_candidates(
        "API key của anh là sk-secret. [[VTS:vts_01]]",
        "Em đã nhận.",
    ) == ()


def test_rejects_empty_input():
    assert extract_candidates("", "") == ()


def test_rejects_url_in_prompt():
    assert extract_candidates("Anh thích https://example.com", "Ok") == ()


def test_very_long_response_does_not_block_learning():
    # Length alone is never "sensitive": gating on length silently disabled
    # self-learning for every long reply.
    candidates = extract_candidates("Anh thích pizza", "x" * 1500)
    assert [(item.kind, item.content) for item in candidates] == [
        (MemoryKind.PREFERENCE, "Anh thích pizza.")
    ]


def test_strip_internal_metadata():
    result = strip_internal_metadata("Hello [[VTS:vts_01]] world")
    assert result == "Hello  world"


def test_strips_desktop_action_markers_before_memory_extraction():
    result = strip_internal_metadata(
        "Anh thích Notepad [APP_OPEN: notepad] [VOLUME: up] [SEARCH: weather]"
    )

    assert result == "Anh thích Notepad"
    assert "[APP_OPEN:" not in result
    assert "[VOLUME:" not in result
    assert "[SEARCH:" not in result


def test_memory_policy_rejects_desktop_action_markers_as_sensitive_metadata():
    for marker in ("[APP_OPEN: notepad]", "[VOLUME: up]", "[SEARCH: weather]"):
        assert is_sensitive_or_unsafe(marker) is True


def test_is_sensitive_rejects_password():
    assert is_sensitive_or_unsafe("password là abc123") is True


def test_is_sensitive_rejects_ghp_token():
    assert is_sensitive_or_unsafe("ghp_abcdef123456") is True


def test_is_sensitive_accepts_normal_text():
    assert is_sensitive_or_unsafe("Anh thích Ru") is False


def test_preference_only_no_decision():
    candidates = extract_candidates("Anh thích pizza", "Em biết rồi.")
    assert len(candidates) == 1
    assert candidates[0].kind == MemoryKind.PREFERENCE
    assert candidates[0].scope is None


def test_decision_without_use_keyword_not_extracted():
    """Scope path exists but no 'sẽ dùng/dùng' keyword → no DECISION."""
    candidates = extract_candidates(
        "D:\\LLM có nhiều file.", "Em thấy rồi."
    )
    assert candidates == ()
