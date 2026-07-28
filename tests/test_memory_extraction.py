from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import extract_candidates


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
