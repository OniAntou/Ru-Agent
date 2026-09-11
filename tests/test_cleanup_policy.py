from __future__ import annotations

import pytest

from tools.maintenance.cleanup_policy import (
    CleanupCategory,
    classify_path,
    normalize_relative_path,
)


@pytest.mark.parametrize(
    "value",
    ("", ".", "../outside", "/outside", r"\\server\share", "D:/LLM/models"),
)
def test_normalize_relative_path_rejects_broad_or_external_paths(value: str) -> None:
    with pytest.raises(ValueError):
        normalize_relative_path(value)


@pytest.mark.parametrize(
    "relative",
    (
        ".git",
        ".worktrees/task",
        ".venv/Lib/site-packages/example.py",
        "omnivoice_env/python.exe",
        "models/omnivoice/model.bin",
        "data/conversations/approved.jsonl",
        "data/conversations/memory.db",
        "data/memory.db",
        ".minigpt/settings.json",
    ),
)
def test_active_runtime_user_data_and_current_design_are_protected(relative: str) -> None:
    decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.PROTECTED


def test_dirty_path_is_protected_before_legacy_classification() -> None:
    relative = "configs/ru_llama3_1_v6_3.json"
    decision = classify_path(relative, tracked=True, dirty_paths=frozenset({relative}))
    assert decision.category is CleanupCategory.PROTECTED
    assert decision.reason == "git_dirty_or_untracked"


def test_candidate_root_is_protected_when_it_contains_a_dirty_path() -> None:
    relative = "data/conversations/ru_v6_2"
    dirty = frozenset({"data/conversations/ru_v6_2/private-change.jsonl"})
    decision = classify_path(relative, tracked=False, dirty_paths=dirty)
    assert decision.category is CleanupCategory.PROTECTED
    assert decision.reason == "git_dirty_or_untracked"


@pytest.mark.parametrize(
    "relative",
    (
        "data/conversations/ru_v1/train.jsonl",
        "data/conversations/ru_v1_1/train.jsonl",
        "data/conversations/ru_v1_2/train.jsonl",
        "configs/ru_llama3_1_v6_3.json",
        "src/ru_runtime/ru_v6_3_corpus.py",
        "src/ru_runtime/_base_corpus.py",
        "src/ru_runtime/_corpus_quality.py",
        "src/ru_runtime/corpus_builder.py",
        "src/ru_runtime/corpus_common.py",
        "src/ru_runtime/corpus_validate.py",
        "src/ru_runtime/ru_hard_evaluation.py",
        "scripts/build_corpus.py",
        "scripts/corpus_baseline_hashes.json",
        "scripts/evaluation_support.py",
        "scripts/verify_corpus_byte_identical.py",
        "tests/test_build_corpus_cli.py",
        "tests/test_corpus_byte_identical.py",
        "tests/test_ru_hard_evaluation.py",
        "tests/test_training_config.py",
        "models/lfm2.5-2.6b-hf/config.json",
        "models/gguf/LFM2.5-2.6B-Q4_K_M.gguf",
        "checkpoints/ru-lfm2.5-2.6b-v1/adapter.json",
        "tools/llama.cpp-bin/llama-server.exe",
        "checkpoints/ru-hard-evaluation-v1",
        "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
        "data/conversations/ru_v6_2_fresh",
        "eval_results/ru_llama3_1_v6_style.json",
    ),
)
def test_retired_local_model_paths_are_classified_as_legacy(relative: str) -> None:
    decision = classify_path(
        relative,
        tracked=relative.startswith(("configs/", "src/", "scripts/", "tests/")),
        dirty_paths=frozenset(),
    )
    assert decision.category is CleanupCategory.LEGACY_LLAMA
    assert decision.recoverability in {"git", "permanent"}


@pytest.mark.parametrize(
    "relative",
    (
        ".venv311",
        "tmp_verify_fresh2",
        "gui-winui3/bin",
        "backend-dotnet/RuBackend/obj",
        "gui-winui3/pri_staging",
        "tools/ru_desktop_overlay/out",
        "tests/__pycache__",
        "rustup-init.exe",
        "resources.pri.xml",
    ),
)
def test_generated_paths_are_rebuildable(relative: str) -> None:
    decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REBUILDABLE


def test_generated_path_containing_tracked_content_is_protected() -> None:
    for relative in ("bin", "bin/ru.cmd"):
        decision = classify_path(relative, tracked=True, dirty_paths=frozenset())
        assert decision.category is CleanupCategory.PROTECTED
        assert decision.reason == "contains_tracked_paths"


def test_vcpkg_requires_review_while_overlay_build_references_it() -> None:
    decision = classify_path(".tmp/vcpkg", tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED


def test_backup_with_non_git_source_requires_review() -> None:
    decision = classify_path("gui-winui3.bak", tracked=False, dirty_paths=frozenset())
    assert decision.category is CleanupCategory.REVIEW_REQUIRED
    assert decision.reason == "contains_non_git_backup"


def test_runtime_logs_require_review_before_permanent_cleanup() -> None:
    for relative in ("logs", ".tmp/lfm25-smoke.err.log"):
        decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
        assert decision.category is CleanupCategory.REVIEW_REQUIRED
        assert decision.reason == "runtime_logs_may_contain_private_data"
