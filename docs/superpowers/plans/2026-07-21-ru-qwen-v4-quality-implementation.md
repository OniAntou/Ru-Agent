# Ru Qwen V4 Conversation Quality Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Build and measure a local Qwen 1.5B V4 candidate that learns only from curated bilingual dialogue and can be adopted only after a fixed conversation-quality gate passes.

**Architecture:** V4 is isolated from the failed V3 experiment through independent base, data, adapter, and evaluation paths. Registered Qwen base profiles prevent an arbitrary remote model from reaching the local trainer; the V4 corpus produces train, validation, and held-out JSONL files; the evaluator compares deterministic base and adapter output before the GUI is allowed to change.

**Tech Stack:** Python 3.11, PyTorch CUDA, Transformers, PEFT, bitsandbytes NF4 QLoRA, pytest, Hugging Face Hub.

---

## File map

| Path | Responsibility |
| --- | --- |
| src/mini_gpt/qwen_runtime.py | Registered Qwen bases, local verification, and deterministic 4-bit generation. |
| scripts/download_ru_qwen_v4_base.py | Downloads and verifies only the V4 Qwen 1.5B base. |
| src/mini_gpt/ru_v4_dialogues.py | The 400 reviewed project-owned dialogue rows. |
| src/mini_gpt/ru_v4_corpus.py | V4 validation, deterministic split, atomic build, and audit. |
| scripts/build_ru_qwen_v4_data.py | Local V4 build/audit command. |
| src/mini_gpt/ru_qwen_evaluation.py | Fixed 32-case suite and pure quality checks. |
| scripts/evaluate_ru_qwen_v4.py | Generates and records base/adapter comparison JSON. |
| src/mini_gpt/qwen_training.py | Profile-bound training and validation-best adapter saving. |
| configs/ru_qwen_v4_1_5b.json | The one-epoch V4 candidate profile. |
| scripts/train_ru_qwen_v4.py | V4 training command. |
| tests/test_qwen_runtime.py | Profile/downloader/generation boundary tests. |
| tests/test_ru_v4_corpus.py | Corpus shape, quality, split, and audit tests. |
| tests/test_ru_qwen_evaluation.py | Suite and automatic-gate tests. |
| tests/test_qwen_training.py | Profile and manifest tests. |
| README.md and TODO.md | Only measured V4 commands and results. |

### Task 1: Register the V4 base and local downloader

**Files:**
- Modify: src/mini_gpt/qwen_runtime.py
- Create: scripts/download_ru_qwen_v4_base.py
- Modify: tests/test_qwen_runtime.py

- [ ] **Step 1: Write failing registered-profile tests**

Add these imports and tests to tests/test_qwen_runtime.py:

~~~
from mini_gpt.qwen_runtime import QWEN_V4_BASE, qwen_base_profile


def test_v4_profile_is_the_registered_local_qwen_1_5b_base():
    assert QWEN_V4_BASE.model_id == "Qwen/Qwen2.5-1.5B-Instruct"
    assert QWEN_V4_BASE.local_path == "models/qwen2.5-1.5b-instruct"
    assert qwen_base_profile(QWEN_V4_BASE.model_id) == QWEN_V4_BASE


def test_unknown_qwen_profile_is_rejected():
    with pytest.raises(ValueError, match="registered Ru Qwen base"):
        qwen_base_profile("other/model")


def test_v4_qwen_base_downloader_exposes_help_without_downloading():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_ru_qwen_v4_base.py"
    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Download and verify Ru's V4 Qwen 1.5B base" in result.stdout
~~~

- [ ] **Step 2: Run the failing tests**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
~~~

Expected: collection fails because the V4 profile, profile resolver, and V4 downloader do not exist.

- [ ] **Step 3: Implement registered profiles**

Replace the one fixed Qwen model-ID surface with this exact immutable profile API in src/mini_gpt/qwen_runtime.py. Retain QWEN_MODEL_ID as the V3 compatibility alias because existing V3 code imports it.

~~~
from dataclasses import dataclass


@dataclass(frozen=True)
class QwenBaseProfile:
    model_id: str
    local_path: str
    label: str


QWEN_V3_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-0.5B-Instruct",
    local_path="models/qwen2.5-0.5b-instruct",
    label="Ru V3 Qwen 0.5B",
)
QWEN_V4_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-1.5B-Instruct",
    local_path="models/qwen2.5-1.5b-instruct",
    label="Ru V4 Qwen 1.5B",
)
QWEN_MODEL_ID = QWEN_V3_BASE.model_id
QWEN_BASE_PROFILES = {profile.model_id: profile for profile in (QWEN_V3_BASE, QWEN_V4_BASE)}


def qwen_base_profile(model_id: str) -> QwenBaseProfile:
    try:
        return QWEN_BASE_PROFILES[model_id]
    except KeyError as error:
        raise ValueError(f"model_id must be a registered Ru Qwen base, got {model_id!r}") from error
~~~

Keep verify_local_qwen_base responsible only for validating a local Qwen2 configuration and safetensors files. It must not infer a Hugging Face repository name from the artifact.

- [ ] **Step 4: Implement a V4-only downloader**

Create scripts/download_ru_qwen_v4_base.py:

~~~
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.qwen_runtime import QWEN_V4_BASE, verify_local_qwen_base  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Download and verify Ru's V4 Qwen 1.5B base")
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / QWEN_V4_BASE.local_path))
    return parser


def report(output_dir: Path) -> dict[str, object]:
    config = verify_local_qwen_base(output_dir)
    return {
        "model_id": QWEN_V4_BASE.model_id,
        "local_path": str(output_dir.resolve()),
        "model_type": config["model_type"],
        "safetensors_bytes": sum(path.stat().st_size for path in output_dir.glob("*.safetensors")),
    }


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    output_dir = Path(args.output_dir)
    try:
        payload = report(output_dir)
    except (FileNotFoundError, ValueError):
        from huggingface_hub import snapshot_download

        snapshot_download(repo_id=QWEN_V4_BASE.model_id, local_dir=str(output_dir))
        payload = report(output_dir)
    print(json.dumps(payload, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
~~~

The script has no model-ID flag. It can only download the registered V4 base and never overwrites the V3 directory.

- [ ] **Step 5: Verify and commit**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_runtime.py -q
git diff --check
git add -- src/mini_gpt/qwen_runtime.py scripts/download_ru_qwen_v4_base.py tests/test_qwen_runtime.py
git commit -m "feat: register Ru Qwen V4 base"
~~~

Expected: tests pass without a model download.

### Task 2: Build the 400-row V4 curated corpus

**Files:**
- Create: src/mini_gpt/ru_v4_dialogues.py
- Create: src/mini_gpt/ru_v4_corpus.py
- Create: scripts/build_ru_qwen_v4_data.py
- Create: tests/test_ru_v4_corpus.py

- [ ] **Step 1: Write failing corpus-shape tests**

Create tests/test_ru_v4_corpus.py with this primary test:

~~~
def test_v4_corpus_has_the_approved_shape(tmp_path):
    report = build_ru_v4_corpus(v4_dialogue_records(), tmp_path, seed=17)

    assert report["total_examples"] == 400
    assert report["split_counts"] == {"train": 320, "validation": 40, "held_out": 40}
    assert report["language_counts"] == {"vi": 160, "en": 160, "mixed": 80}
    assert report["contextual_examples"] >= 120
    assert report["direct_acknowledgements"] >= 100
    assert report["everyday_help_examples"] >= 60
    assert report["source_counts"] == {"teacher-curated-project-owned": 400}
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert (tmp_path / "held_out.jsonl").is_file()
~~~

Add tests that reject 399 rows, a duplicate dialogue, an invalid response mode, a row without alternating roles, and a Vietnamese default-voice response containing toi/mình/bạn rather than em/anh. Add a split test that reads all three JSONL files and proves each messages-plus-response key occurs exactly once.

- [ ] **Step 2: Run the failing test**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_v4_corpus.py -q
~~~

Expected: collection fails because the V4 modules do not exist.

- [ ] **Step 3: Implement the V4 schema and deterministic builder**

Use these constants in src/mini_gpt/ru_v4_corpus.py:

~~~
EXPECTED_EXAMPLES = 400
SPLIT_COUNTS = {"train": 320, "validation": 40, "held_out": 40}
LANGUAGE_COUNTS = {"vi": 160, "en": 160, "mixed": 80}
MINIMUM_CONTEXTUAL_EXAMPLES = 120
MINIMUM_DIRECT_ACKNOWLEDGEMENTS = 100
MINIMUM_EVERYDAY_HELP = 60
VALID_MODES = {"acknowledge", "follow_up", "everyday_help", "story", "light_humour", "correction"}
SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
~~~

Each source record contains exactly messages, response, language, mode, source, and license. Reuse normalize_text, RU_SYSTEM_PROMPT, and the V3 exclusions for links, code/markup, lists, and AI disclaimers. Build a duplicate key from serialized messages plus response.

Validate every count before creating the output directory. Shuffle a copied record list with Random(seed), assign rows 0..39 to held_out, 40..79 to validation, and 80..399 to train. Write train.jsonl, val.jsonl, held_out.jsonl, and manifest.json atomically with partial files and os.replace. The manifest stores split counts, language counts, mode counts, three minimum counters, source counts, licenses, source checksum, and seed.

- [ ] **Step 4: Author the literal reviewed data source**

Implement v4_dialogue_records() in src/mini_gpt/ru_v4_dialogues.py. Store every retained dialogue explicitly in the tracked source file; no template-generated text is allowed at build time.

The exact final source contract is:

| Requirement | Exact count |
| --- | ---: |
| Total rows | 400 |
| Vietnamese | 160 |
| English | 160 |
| Light code-switching | 80 |
| Rows with 2-4 earlier turns | at least 120 |
| acknowledge mode without a closing question | at least 100 |
| everyday_help mode | at least 60 |

Use at least 100 acknowledge, 100 follow_up, 60 everyday_help, 60 story, 40 light_humour, and 40 correction rows. All Vietnamese default rows use em for Ru and anh for the user. Each language includes greetings, routine daily life, tiredness, a game, food, weather, quiet evenings, a small success, light frustration, and an ongoing conversation. Check every reply for direct relevance and vary endings so questions are not a default pattern.

- [ ] **Step 5: Add local build/audit CLI and verify**

Create scripts/build_ru_qwen_v4_data.py with --output-dir defaulting to data/conversations/ru_v4, --seed defaulting to 42, and --audit. Audit prints all 40 held-out rows and a deterministic stratified sample from the other splits; it never downloads data.

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_v4_corpus.py -q
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --output-dir data\conversations\ru_v4
git diff --check
~~~

Expected: all tests pass; audit shows 40 held-out rows, 400 unique rows, and no prohibited content.

- [ ] **Step 6: Commit the corpus**

Run:

~~~
git add -- src/mini_gpt/ru_v4_dialogues.py src/mini_gpt/ru_v4_corpus.py scripts/build_ru_qwen_v4_data.py tests/test_ru_v4_corpus.py
git commit -m "feat: build curated Ru V4 conversation corpus"
~~~

### Task 3: Add the pre-training conversation-quality gate

**Files:**
- Create: src/mini_gpt/ru_qwen_evaluation.py
- Create: scripts/evaluate_ru_qwen_v4.py
- Create: tests/test_ru_qwen_evaluation.py
- Modify: src/mini_gpt/qwen_runtime.py
- Modify: tests/test_qwen_runtime.py

- [ ] **Step 1: Write failing suite and assessment tests**

Create tests/test_ru_qwen_evaluation.py with these tests:

~~~
from mini_gpt.ru_qwen_evaluation import V4_CONVERSATION_SUITE, assess_response


def test_v4_suite_has_a_fixed_bilingual_shape():
    assert len(V4_CONVERSATION_SUITE) == 32
    assert sum(case.language == "vi" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "en" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "mixed" for case in V4_CONVERSATION_SUITE) == 4
    assert len({case.case_id for case in V4_CONVERSATION_SUITE}) == 32


def test_assess_response_rejects_echo_empty_disclaimer_and_wrong_vietnamese_voice():
    assert not assess_response("Xin chào em", "Xin chào em", "vi").passed
    assert not assess_response("Anh hơi mệt", "", "vi").passed
    assert not assess_response("Hello", "As an AI language model, I cannot help.", "en").passed
    assert not assess_response("Anh hơi mệt", "Tôi hiểu rồi, bạn nên nghỉ.", "vi").passed


def test_assess_response_accepts_a_natural_default_vietnamese_reply():
    assessment = assess_response("Anh hơi mệt", "Em hiểu, tối nay anh nghỉ một chút nha.", "vi")
    assert assessment.passed
    assert assessment.failures == ()
~~~

Add a context test that requires a previous assistant turn and a self-repetition test using one identical sentence twice.

- [ ] **Step 2: Run the failing tests**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_evaluation.py -q
~~~

Expected: collection fails because V4_CONVERSATION_SUITE and assess_response do not exist.

- [ ] **Step 3: Implement literal evaluation cases and pure assessments**

Create a frozen ConversationCase dataclass with case_id, language, messages, and prompt. Populate V4_CONVERSATION_SUITE with 32 literal cases: 14 Vietnamese, 14 English, and 4 light code-switching. Each case starts with RU_SYSTEM_PROMPT and ends with the exact user prompt. Include greeting, tiredness, a small story, direct question, mild frustration, positive news, language change, Vietnamese pronoun challenge, and six context-dependent cases.

Create ResponseAssessment and implement:

~~~
def assess_response(prompt: str, response: str, language: str) -> ResponseAssessment:
    normalized_prompt = normalize_text(prompt).casefold()
    normalized_response = normalize_text(response)
    failures: list[str] = []
    if not normalized_response:
        failures.append("empty")
    if normalized_response.casefold() == normalized_prompt:
        failures.append("echo")
    if AI_DISCLAIMER_PATTERN.search(normalized_response):
        failures.append("ai_disclaimer")
    if _has_repeated_sentence(normalized_response):
        failures.append("self_repetition")
    if language == "vi" and _violates_default_vietnamese_voice(normalized_response):
        failures.append("vietnamese_voice")
    return ResponseAssessment(passed=not failures, failures=tuple(failures))
~~~

The Vietnamese voice check rejects word-boundary toi, mình, and bạn in default Vietnamese output. It allows the one explicit-form-change case only when the case metadata marks it. No external language detector or model judge is used.

- [ ] **Step 4: Add deterministic Qwen generation**

Add generate_qwen_response to src/mini_gpt/qwen_runtime.py. It must require a QwenBaseProfile, verify its local model path, load 4-bit NF4, optionally load a PEFT adapter only when its directory exists, apply the tokenizer chat template, and decode only generated tokens.

Its generation call is fixed:

~~~
model.generate(
    **encoded,
    do_sample=False,
    max_new_tokens=96,
    repetition_penalty=1.05,
)
~~~

Add a monkeypatched test proving those three generation parameters are passed. Pytest must not load an actual model or CUDA.

- [ ] **Step 5: Implement evaluator command and commit**

Create scripts/evaluate_ru_qwen_v4.py. It reads the V4 profile, runs base output and optional adapter output for every case, writes atomic JSON containing prompt, responses, and assessments, and returns non-zero when a requested adapter fails any automatic check.

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_evaluation.py tests\test_qwen_runtime.py -q
git diff --check
git add -- src/mini_gpt/ru_qwen_evaluation.py scripts/evaluate_ru_qwen_v4.py src/mini_gpt/qwen_runtime.py tests/test_ru_qwen_evaluation.py tests/test_qwen_runtime.py
git commit -m "feat: evaluate Ru V4 conversation quality"
~~~

Expected: pure tests pass and no model download occurs.

### Task 4: Make QLoRA training profile-aware and validation-best

**Files:**
- Modify: src/mini_gpt/qwen_training.py
- Create: configs/ru_qwen_v4_1_5b.json
- Create: scripts/train_ru_qwen_v4.py
- Modify: tests/test_qwen_training.py

- [ ] **Step 1: Write failing V4 training tests**

Replace the V3-only rejection test with:

~~~
from mini_gpt.qwen_runtime import QWEN_V4_BASE


def test_training_config_accepts_registered_v4_base_and_rejects_unknown_model():
    config = QwenTrainingConfig(base_model_id=QWEN_V4_BASE.model_id)
    assert config.base_model_path == QWEN_V4_BASE.local_path

    with pytest.raises(ValueError, match="registered Ru Qwen base"):
        QwenTrainingConfig(base_model_id="other/model")


def test_adapter_manifest_records_the_candidate_backend_and_profile(tmp_path):
    manifest = write_adapter_manifest(
        tmp_path,
        backend="qwen-qlora-v4",
        base_model_id=QWEN_V4_BASE.model_id,
        base_model_path="D:/LLM/models/qwen2.5-1.5b-instruct",
        dataset_sha256="abc",
        best_validation_loss=1.25,
        peak_vram_mb=1200.0,
        seed=42,
        max_length=512,
        profile_name="ru_qwen_v4_1_5b",
    )

    assert manifest["backend"] == "qwen-qlora-v4"
    assert manifest["profile_name"] == "ru_qwen_v4_1_5b"
~~~

- [ ] **Step 2: Run the failing tests**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_training.py -q
~~~

Expected: current V3-only model validation and manifest signature fail.

- [ ] **Step 3: Implement profile validation and optimizer-boundary evaluation**

Change the frozen config field to base_model_path: str | None = None and add
backend: str = "qwen-qlora-v3" and profile_name: str = "ru_qwen_v3". Use this
exact defaulting logic in QwenTrainingConfig.__post_init__ so the existing V3
JSON remains valid while a V4 config receives its own registered default:

~~~
profile = qwen_base_profile(self.base_model_id)
if self.base_model_path is None:
    object.__setattr__(self, "base_model_path", profile.local_path)
elif self.base_model_path.replace("\\", "/") != profile.local_path:
    raise ValueError("base_model_path must match the registered Ru Qwen base path")
~~~

Add backend and profile_name to the adapter manifest. The V3 config keeps its
defaults; the V4 JSON supplies qwen-qlora-v4 and ru_qwen_v4_1_5b explicitly.

Factor validation into:

~~~
def validation_loss() -> float:
    model.eval()
    losses: list[float] = []
    with torch.inference_mode():
        for row in validation_rows:
            batch = {key: value.to("cuda") for key, value in collator([row]).items()}
            losses.append(float(model(**batch).loss))
    model.train()
    return sum(losses) / len(losses)
~~~

Run validation after every completed optimizer update whose count is divisible by evaluation_steps and once at the final update. Save the adapter only when the newly measured validation loss improves the current best. Continue to divide loss by gradient_accumulation_steps exactly once before backward.

- [ ] **Step 4: Add the isolated V4 config and entrypoint**

Create configs/ru_qwen_v4_1_5b.json:

~~~
{
  "backend": "qwen-qlora-v4",
  "profile_name": "ru_qwen_v4_1_5b",
  "base_model_id": "Qwen/Qwen2.5-1.5B-Instruct",
  "base_model_path": "models/qwen2.5-1.5b-instruct",
  "dataset_dir": "data/conversations/ru_v4",
  "adapter_dir": "checkpoints/ru-qwen-v4/adapter",
  "max_length": 512,
  "seed": 42,
  "epochs": 1,
  "learning_rate": 0.00001,
  "batch_size": 1,
  "gradient_accumulation_steps": 8,
  "evaluation_steps": 5,
  "lora_rank": 16,
  "lora_alpha": 32,
  "lora_dropout": 0.05,
  "target_modules": ["q_proj", "k_proj", "v_proj", "o_proj"]
}
~~~

Create scripts/train_ru_qwen_v4.py as the V3 CLI equivalent, defaulting to this config and retaining --max-steps for the CUDA smoke run.

- [ ] **Step 5: Verify and commit**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest tests\test_qwen_training.py tests\test_qwen_data.py -q
git diff --check
git add -- src/mini_gpt/qwen_training.py configs/ru_qwen_v4_1_5b.json scripts/train_ru_qwen_v4.py tests/test_qwen_training.py
git commit -m "feat: train isolated Ru Qwen V4 candidate"
~~~

Expected: tests pass without CUDA or a local Qwen model.

### Task 5: Measure V4 locally and decide adoption

**Files:**
- Runtime create: models/qwen2.5-1.5b-instruct/* (ignored)
- Runtime create: data/conversations/ru_v4/* (ignored)
- Runtime create: checkpoints/ru-qwen-v4/adapter/* (ignored)
- Runtime create: checkpoints/ru-qwen-v4/base_evaluation.json and evaluation.json (ignored)

- [ ] **Step 1: Measure the machine before downloading**

Run:

~~~
Get-PSDrive -Name D | Select-Object Name,Free
& .\.venv\Scripts\python.exe scripts\qwen_preflight.py
~~~

Expected: CUDA is available and at least 5 GB remains on D after the V4 base download. Stop and report if CUDA is unavailable, the download leaves less than 5 GB free, or the later smoke run exceeds 5500 MiB. Never delete V3 artifacts to make room.

- [ ] **Step 2: Download and verify only the V4 base**

Run:

~~~
& .\.venv\Scripts\python.exe scripts\download_ru_qwen_v4_base.py
& .\.venv\Scripts\python.exe -c "from mini_gpt.qwen_runtime import verify_local_qwen_base; print(verify_local_qwen_base(r'D:\LLM\models\qwen2.5-1.5b-instruct')['model_type'])"
~~~

Expected: the report names Qwen/Qwen2.5-1.5B-Instruct, has positive safetensors bytes, and local validation prints qwen2.

- [ ] **Step 3: Build data and record the base baseline**

Run:

~~~
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v4_data.py --output-dir data\conversations\ru_v4
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json --output checkpoints\ru-qwen-v4\base_evaluation.json
~~~

Expected: the audit includes all 40 held-out records; base evaluation writes all 32 cases. If the base itself fails an automatic check, retain that evidence and stop before training to inspect the profile or prompt.

- [ ] **Step 4: Run the bounded CUDA smoke test**

Run:

~~~
& .\.venv\Scripts\python.exe scripts\train_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json --max-steps 2
~~~

Expected: adapter_config.json and ru_manifest.json exist under checkpoints/ru-qwen-v4/adapter and peak_vram_mb is at most 5500. Stop on OOM, an incomplete adapter, or an over-limit peak.

- [ ] **Step 5: Train once and evaluate side by side**

Run:

~~~
& .\.venv\Scripts\python.exe scripts\train_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_v4.py --config configs\ru_qwen_v4_1_5b.json --adapter-dir checkpoints\ru-qwen-v4\adapter --output checkpoints\ru-qwen-v4\evaluation.json
~~~

Expected: a validation-best manifest and a 32-case base/adapter JSON. Any automatic adapter failure means not adopted; the GUI remains unchanged.

- [ ] **Step 6: Perform manual quality review**

Score every adapter response in evaluation.json natural or not-natural for relevance, flow, warmth, language choice, and Vietnamese anh/em voice. Adopt only if every automatic check passes, at least 28 of 32 responses are natural, and the adapter is no worse than the base in the remaining cases. Record adopted or not_adopted in the evaluation JSON; never use validation loss as the adoption verdict.

### Task 6: Update documentation from the measured result

**Files:**
- Modify: README.md
- Modify: TODO.md
- Modify: docs/superpowers/specs/2026-07-21-ru-qwen-v4-quality-design.md only if measured conditions require a factual correction

- [ ] **Step 1: Update README with facts only**

Add a Ru V4 result subsection under the Qwen section after Task 5. Include the exact model ID, dataset split counts, measured peak VRAM, base and adapter evaluation artifact paths, and one exact verdict: adopted or not adopted. Do not claim a V4 GUI exists unless a separate later task implements and tests it.

- [ ] **Step 2: Make TODO current and retain diagnostic history**

Leave V2 and V3 marked as quality-gate failures. Mark V4 complete only after the real evaluation. If V4 fails, leave Improve Ru from recorded V4 evaluation failures unchecked. If V4 passes, leave Add a Qwen V4 GUI runner after a separate runtime smoke test unchecked. Do not delete older design documents; they record rejected paths and remain useful evidence.

- [ ] **Step 3: Verify docs, test suite, and commit**

Run:

~~~
& .\.venv\Scripts\python.exe -m pytest -q
rg -n -i "ru-qwen-v4|qwen2.5-1.5b|not adopted|adopted|quality gate" README.md TODO.md docs\superpowers\specs
git diff --check
git status --short
git add -- README.md TODO.md docs/superpowers/specs/2026-07-21-ru-qwen-v4-quality-design.md
git commit -m "docs: record Ru V4 evaluation result"
~~~

Expected: the complete test suite passes, documentation reflects only measured V4 state, ignored runtime artifacts remain untracked, and no unrelated change is staged.
