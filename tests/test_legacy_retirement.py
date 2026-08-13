from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

LEGACY_TRACKED_PATHS = (
    "configs/ru_llama3_1_8b.json",
    "configs/ru_llama3_1_8b_v6.json",
    "configs/ru_llama3_1_8b_v6_1.json",
    "configs/ru_llama3_1_8b_v6_1_smoke.json",
    "configs/ru_llama3_1_hard_eval.json",
    "configs/ru_llama3_1_v6_2_fresh.json",
    "configs/ru_llama3_1_v6_3.json",
    "scripts/audit_ru_v6_1_naturalness.py",
    "scripts/benchmark_ru_v6_gguf.py",
    "scripts/build_corpus.py",
    "scripts/build_ru_v6_gguf.py",
    "scripts/chat_ru_llama3_1.py",
    "scripts/corpus_baseline_hashes.json",
    "scripts/download_llama3_1_hf.py",
    "scripts/evaluate_ru_llama3_1_hard.py",
    "scripts/evaluate_ru_llama3_1_prompt_routing.py",
    "scripts/evaluate_ru_llama3_1_qlora.py",
    "scripts/evaluation_support.py",
    "scripts/merge_approved_into_ru_v6_1.py",
    "scripts/train_ru_llama3_1.py",
    "scripts/verify_corpus_byte_identical.py",
    "src/ru_runtime/_base_corpus.py",
    "src/ru_runtime/_corpus_quality.py",
    "src/ru_runtime/approved_v6_1_import.py",
    "src/ru_runtime/corpus_builder.py",
    "src/ru_runtime/corpus_common.py",
    "src/ru_runtime/corpus_validate.py",
    "src/ru_runtime/llama3_1_runtime.py",
    "src/ru_runtime/ru_hard_evaluation.py",
    "src/ru_runtime/ru_v6_1_corpus.py",
    "src/ru_runtime/ru_v6_1_dialogues.py",
    "src/ru_runtime/ru_v6_1_naturalness.py",
    "src/ru_runtime/ru_v6_2_corpus.py",
    "src/ru_runtime/ru_v6_2_dialogues.py",
    "src/ru_runtime/ru_v6_2_fresh_corpus.py",
    "src/ru_runtime/ru_v6_2_fresh_dialogues.py",
    "src/ru_runtime/ru_v6_2_fresh_import.py",
    "src/ru_runtime/ru_v6_3_corpus.py",
    "src/ru_runtime/ru_v6_3_dialogues.py",
    "src/ru_runtime/ru_v6_corpus.py",
    "tests/test_build_corpus_cli.py",
    "tests/test_corpus_byte_identical.py",
    "tests/test_ru_hard_evaluation.py",
    "tests/test_ru_v6_2_fresh_corpus.py",
    "tests/test_ru_v6_2_fresh_dialogues.py",
    "tests/test_ru_v6_2_fresh_import.py",
    "tests/test_ru_v6_3_corpus.py",
    "tests/test_ru_v6_3_dialogues.py",
    "tests/test_training_config.py",
)

_LEGACY_IMPORT = re.compile(
    r"^\s*(?:from|import)\s+[^#\n]*(?:ru_v6|ru_llama_evaluation|"
    r"llama3_1_runtime|ru_hard_evaluation|approved_v6_1_import|corpus_builder)",
    re.MULTILINE,
)


def test_approved_tracked_legacy_paths_are_absent() -> None:
    remaining = [relative for relative in LEGACY_TRACKED_PATHS if (ROOT / relative).exists()]
    assert remaining == []


def test_current_python_has_no_retired_legacy_imports() -> None:
    roots = (ROOT / "src" / "ru_runtime", ROOT / "scripts", ROOT / "tests")
    protected = {
        ROOT / "src" / "ru_runtime" / "llama3_1_training.py",
        Path(__file__).resolve(),
    }
    offenders = []
    for root in roots:
        for path in root.glob("*.py"):
            if path in protected:
                continue
            if _LEGACY_IMPORT.search(path.read_text(encoding="utf-8")):
                offenders.append(path.relative_to(ROOT).as_posix())
    assert offenders == []


def test_active_llama_cpp_and_protected_training_seams_remain() -> None:
    assert (ROOT / "src/ru_runtime/llama_cpp_runtime.py").is_file()
    assert (ROOT / "tests/test_llama_cpp_runtime.py").is_file()
    assert (ROOT / "src/ru_runtime/llama3_1_training.py").is_file()


def test_ci_no_longer_executes_the_retired_corpus_verifier() -> None:
    workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    assert "verify_corpus_byte_identical" not in workflow
