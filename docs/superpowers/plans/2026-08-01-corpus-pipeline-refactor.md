# Corpus Pipeline Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Giảm ~1.400 dòng trùng lặp trong corpus pipeline (5 builder + 4 validator + 5 script build → 1 shared machinery + 1 parameterized validator + 1 CLI), giữ output JSONL **byte-for-byte** so với `data/conversations/*` hiện có.

**Architecture:** Tách machinery giống hệt (Scene/SCENES, times, stances, VI/EN acks/details/questions, slot/record/history machinery) của v6.1/v6.2 vào `corpus_common.py`; mỗi version chỉ giữ phần prompt content khác (frames, tails, mixed_*, `_response`). Gộp validator v6.1/v6.2 trùng ~90% thành `corpus_validate.build_corpus(...)` tham số hóa bằng flags; 2 file corpus cũ thành thin wrappers giữ nguyên tên hàm. Fresh/v6.3 GIỮ NGUYÊN (logic normalize/report khác hẳn — điều khoản dự phòng spec §4.3). 1 CLI `build_corpus.py --version ...` thay 5 script cũ. Gate `verify_corpus_byte_identical.py` (SHA256 4 file × 5 version so data hiện có) là tiêu chí thành công — chạy được ngay từ Task 1 như baseline.

**Tech Stack:** Python 3.13, stdlib (argparse, random.Random, dataclasses, collections.Counter), pytest.

## Global Constraints

- **Byte-for-byte:** Output JSONL (train/val/held_out/manifest) phải GIỮ NGUYÊN SHA256 so với `data/conversations/<dir>/` hiện có. Không đổi seed (v6/v6.1/v6.2=42, fresh=62, v6.3=63), không đổi thứ tự sinh, không đổi thứ tự key trong dict report (dict insertion order ảnh hưởng manifest.json).
- **Không đụng:** `ru_v6_dialogues.py`, `ru_v6_2_fresh_dialogues.py`, `ru_v6_3_dialogues.py`, `ru_v6_2_fresh_corpus.py`, `ru_v6_3_corpus.py`, `ru_v6_2_fresh_import.py`, `approved_v6_1_import.py`, `scripts/merge_approved_into_ru_v6_1.py`, `scripts/audit_ru_v6_1_naturalness.py` (import `v6_1_dialogue_records` — tên hàm này BẮT BUỘC giữ), `configs/`, `data/`, `_base_corpus.py`, `_text_utils.py`, `_corpus_quality.py`, `_safety_patterns.py`, `ru_style.py`.
- **Điểm neo test phải giữ:** `build_ru_v6_1_corpus(output_dir, *, seed=42, write=True)`, `build_ru_v6_2_corpus(output_dir, *, seed=42, write=True)`, `build_ru_v6_2_fresh_corpus(...)`, `build_ru_v6_3_corpus(...)`, `v6_1_dialogue_records()`, `v6_2_dialogue_records()`, `V6_1_ROWS`, `V6_2_ROWS`, các `EXPECTED_*`/`VALID_*` exports của `ru_v6_1_corpus`/`ru_v6_2_corpus` (không test nào import chúng nhưng scripts cũ dùng).
- **Commit từng task:** conventional `refactor(corpus): ...`; gate PASS trước khi commit.
- Error messages giữ nguyên văn (dùng `{version}` interpolated như cũ: v6.1/v6.2).
- Chạy test: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short` (152 test hiện có).
- Gate: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py` → toàn bộ PASS.

---

### Task 1: Verify gate (baseline) — `verify_corpus_byte_identical.py` + test

**Files:**
- Create: `scripts/verify_corpus_byte_identical.py`
- Create: `tests/test_corpus_byte_identical.py`
- Test: `tests/test_corpus_byte_identical.py`

**Interfaces:**
- Produces: `verify_corpus_byte_identical.py --version {v6,v6_1,v6_2,v6_2_fresh,v6_3}` → exit 0 khi mọi version PASS/SKIP, exit 1 khi có FAIL. Đây là gate cho mọi task sau.

- [ ] **Step 1: Viết `scripts/verify_corpus_byte_identical.py`**

```python
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v6_corpus import build_ru_v6_corpus
from mini_gpt.ru_v6_dialogues import v6_dialogue_records
from mini_gpt.ru_v6_1_corpus import build_ru_v6_1_corpus
from mini_gpt.ru_v6_2_corpus import build_ru_v6_2_corpus
from mini_gpt.ru_v6_2_fresh_corpus import build_ru_v6_2_fresh_corpus
from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus

DATA_ROOT = PROJECT_ROOT / "data" / "conversations"
SPLIT_FILES = ("train.jsonl", "val.jsonl", "held_out.jsonl", "manifest.json")

VERSIONS = {
    "v6": {"dir": "ru_v6", "seed": 42},
    "v6_1": {"dir": "ru_v6_1", "seed": 42},
    "v6_2": {"dir": "ru_v6_2", "seed": 42},
    "v6_2_fresh": {"dir": "ru_v6_2_fresh", "seed": 62},
    "v6_3": {"dir": "ru_v6_3", "seed": 63},
}


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _build_into(version: str, destination: Path, seed: int) -> None:
    if version == "v6":
        build_ru_v6_corpus(v6_dialogue_records(), destination, seed=seed)
    elif version == "v6_1":
        build_ru_v6_1_corpus(destination, seed=seed)
    elif version == "v6_2":
        build_ru_v6_2_corpus(destination, seed=seed)
    elif version == "v6_2_fresh":
        build_ru_v6_2_fresh_corpus(
            destination,
            approved_file=DATA_ROOT / "approved.jsonl",
            memory_file=DATA_ROOT / "memory_texts.json",
            seed=seed,
        )
    elif version == "v6_3":
        build_ru_v6_3_corpus(destination, seed=seed)
    else:
        raise ValueError(f"unknown version: {version}")


def verify_version(version: str) -> tuple[str, list[str]]:
    spec = VERSIONS[version]
    existing = DATA_ROOT / spec["dir"]
    if not existing.is_dir():
        return "SKIP", [f"no existing data at {existing}"]
    problems: list[str] = []
    with TemporaryDirectory(prefix=f"verify-{version}-") as temporary_dir:
        _build_into(version, Path(temporary_dir), spec["seed"])
        for name in SPLIT_FILES:
            old = existing / name
            new = Path(temporary_dir) / name
            if not old.is_file():
                problems.append(f"missing existing file: {old}")
                continue
            old_hash = _sha256_file(old)
            new_hash = _sha256_file(new)
            if old_hash != new_hash:
                problems.append(f"{name}: hash mismatch (existing={old_hash[:16]}... rebuilt={new_hash[:16]}...)")
    return ("FAIL" if problems else "PASS"), problems


def main(argv: list[str] | None = None) -> int:
    import argparse

    parser = argparse.ArgumentParser(description="Verify rebuilt corpora are byte-identical to existing data")
    parser.add_argument("--version", choices=sorted(VERSIONS), action="append")
    parser.add_argument("--json", action="store_true", help="print machine-readable results")
    args = parser.parse_args(argv)

    selected = sorted(args.version) if args.version else sorted(VERSIONS)
    results: dict[str, str] = {}
    failures: list[str] = []
    for version in selected:
        status, problems = verify_version(version)
        results[version] = status
        if status == "FAIL":
            failures.extend(f"{version}: {problem}" for problem in problems)
        if args.json:
            continue
        print(f"[{status:4s}] {version}")
        for problem in problems:
            print(f"        {problem}")

    if args.json:
        print(json.dumps({"results": results, "failures": failures}, ensure_ascii=False, indent=2))
    if failures:
        print("BYTE-IDENTICAL FAILURES:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("BYTE-IDENTICAL-OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 2: Chạy gate baseline — phải PASS**

Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py`
Expected: `[PASS]` cho v6, v6_1, v6_2, v6_2_fresh, v6_3 và kết thúc `BYTE-IDENTICAL-OK`, exit 0.
Nếu có FAIL ngay baseline (vd manifest cũ build từ path khác): DỪNG LẠI, báo cáo, xử lý từng case trước khi làm tiếp (không tự ý đổi data).

- [ ] **Step 3: Viết `tests/test_corpus_byte_identical.py`**

```python
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

VERSION_DIRS = {
    "v6": "ru_v6",
    "v6_1": "ru_v6_1",
    "v6_2": "ru_v6_2",
    "v6_2_fresh": "ru_v6_2_fresh",
    "v6_3": "ru_v6_3",
}


@pytest.mark.parametrize("version", sorted(VERSION_DIRS))
def test_corpus_rebuilds_byte_identical(version: str) -> None:
    existing = ROOT / "data" / "conversations" / VERSION_DIRS[version]
    if not existing.is_dir():
        pytest.skip(f"no existing corpus data for {version}")
    result = subprocess.run(
        [sys.executable, "scripts/verify_corpus_byte_identical.py", "--version", version],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert result.returncode == 0, f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
```

- [ ] **Step 4: Chạy test mới + full suite**

Run: `.venv\Scripts\python.exe -m pytest tests\test_corpus_byte_identical.py -v --tb=short`
Expected: 5 passed (không skip — data đầy đủ trên máy dev).
Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 157 passed (152 cũ + 5 mới), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add scripts/verify_corpus_byte_identical.py tests/test_corpus_byte_identical.py
git commit -m "refactor(corpus): add byte-identical rebuild gate for all corpus versions"
```

---

### Task 2: `corpus_common.py` — shared machinery v6.1/v6.2 + chuyển `ru_v6_1_dialogues.py`

**Files:**
- Create: `src/mini_gpt/corpus_common.py`
- Modify: `src/mini_gpt/ru_v6_1_dialogues.py` (724 → ~330 dòng)
- Test: `tests/test_corpus_byte_identical.py` (gate v6.1)

**Interfaces:**
- Consumes: `ru_style.RU_SYSTEM_PROMPT`
- Produces: `Scene`, `SCENES`, `VI_TIMES`/`EN_TIMES`/`MIXED_TIMES`, `VI_STANCES`/`EN_STANCES`/`MIXED_STANCES`, `VI_ACKS`/`VI_DETAILS`/`VI_QUESTIONS`, `EN_ACKS`/`EN_DETAILS`/`EN_QUESTIONS`, `EXPECTED_LANGUAGE_COUNTS`, `EXPECTED_MODE_COUNTS`, `EXPECTED_INTENSITY_COUNTS`, `EXPECTED_TURN_BAND_COUNTS`, `LANGUAGE_MODE_COUNTS`, `turn_count(turn_band: str, index: int) -> int`, `record(language, mode, turns, response, intensity, turn_band, *, self_reference, source, license_name, system_prompt=RU_SYSTEM_PROMPT) -> dict`, `history_user(language, scene, step, index) -> str`, `history_assistant(language, scene, step, index) -> str`, `final_prompt(language, mode, scene, index, *, vi_frames, en_frames, mixed_frames, vi_tails, en_tails, mixed_tails, identity_repair_en) -> str`, `build_slots() -> list[dict[str, str]]`, `build_rows(*, vi_frames, en_frames, mixed_frames, vi_tails, en_tails, mixed_tails, response_fn, self_reference, source, license_name, identity_repair_en) -> list[dict]`

- [ ] **Step 1: Tạo `src/mini_gpt/corpus_common.py`**

Tạo file với nội dung sau (các block `# <name> — di chuyển nguyên văn từ ru_v6_1_dialogues.py:<lines>` là COPY-PASTE KHÔNG SỬA nội dung, chỉ đổi tên module-scope khi được chỉ rõ):

```python
from __future__ import annotations

import re
from dataclasses import dataclass

from .ru_style import RU_SYSTEM_PROMPT


@dataclass(frozen=True)
class Scene:
    vi_event: str
    en_event: str
    vi_noun: str
    en_noun: str
    vi_feeling: str
    en_feeling: str


# _SCENES — di chuyển nguyên văn từ ru_v6_1_dialogues.py:50-99 (48 Scene tuples)
SCENES = (
    # ... copy-paste toàn bộ tuple 48 scenes từ ru_v6_1_dialogues.py:50-99 ...
)

# _VI_TIMES/_EN_TIMES/_MIXED_TIMES — nguyên văn ru_v6_1_dialogues.py:101-103
VI_TIMES = ("tối nay", "lúc này", "sau một ngày dài", "trước khi ngủ", "trong lúc nghỉ", "sáng mai", "cuối tuần này", "khi anh có thêm thời gian")
EN_TIMES = ("tonight", "right now", "after a long day", "before bed", "during this break", "tomorrow morning", "this weekend", "when I have more time")
MIXED_TIMES = ("tối nay", "right now", "sau một ngày dài", "before bed", "trong lúc nghỉ", "tomorrow morning", "cuối tuần này", "when anh có thêm thời gian")

# _VI_STANCES/_EN_STANCES/_MIXED_STANCES — nguyên văn ru_v6_1_dialogues.py:105-134
VI_STANCES = (
    # ... 8 items từ ru_v6_1_dialogues.py:105-114 ...
)
EN_STANCES = (
    # ... 8 items từ ru_v6_1_dialogues.py:115-124 ...
)
MIXED_STANCES = (
    # ... 8 items từ ru_v6_1_dialogues.py:125-134 ...
)

# _VI_ACKS/_VI_DETAILS/_VI_QUESTIONS — nguyên văn ru_v6_1_dialogues.py:245-274
VI_ACKS = (
    # ... 8 items từ ru_v6_1_dialogues.py:245-254 ...
)
VI_DETAILS = (
    # ... 8 items từ ru_v6_1_dialogues.py:255-264 ...
)
VI_QUESTIONS = (
    # ... 8 items từ ru_v6_1_dialogues.py:265-274 ...
)

# _EN_ACKS/_EN_DETAILS/_EN_QUESTIONS — nguyên văn ru_v6_1_dialogues.py:276-305
EN_ACKS = (
    # ... 8 items từ ru_v6_1_dialogues.py:276-285 ...
)
EN_DETAILS = (
    # ... 8 items từ ru_v6_1_dialogues.py:286-295 ...
)
EN_QUESTIONS = (
    # ... 8 items từ ru_v6_1_dialogues.py:296-305 ...
)

# Quota constants — nguyên văn ru_v6_1_dialogues.py:14-35
EXPECTED_LANGUAGE_COUNTS = {"vi": 1200, "mixed": 720, "en": 480}
EXPECTED_MODE_COUNTS = {
    "everyday_follow_up": 900,
    "story_context": 480,
    "emotion_support": 360,
    "language_switch": 240,
    "identity_repair": 180,
    "humour": 120,
    "light_flirt": 120,
}
EXPECTED_INTENSITY_COUNTS = {"neutral": 2040, "light": 288, "adult_boundary": 72}
EXPECTED_TURN_BAND_COUNTS = {"short": 480, "medium": 1200, "long": 720}

LANGUAGE_MODE_COUNTS = {
    "everyday_follow_up": {"vi": 450, "mixed": 270, "en": 180},
    "story_context": {"vi": 240, "mixed": 144, "en": 96},
    "emotion_support": {"vi": 180, "mixed": 108, "en": 72},
    "language_switch": {"vi": 120, "mixed": 72, "en": 48},
    "identity_repair": {"vi": 90, "mixed": 54, "en": 36},
    "humour": {"vi": 60, "mixed": 36, "en": 24},
    "light_flirt": {"vi": 60, "mixed": 36, "en": 24},
}


def turn_count(turn_band: str, index: int) -> int:
    if turn_band == "short":
        return 2 + index % 2
    if turn_band == "medium":
        return 4 + index % 5
    return 9 + index % 6


def record(
    language: str,
    mode: str,
    turns: list[tuple[str, str]],
    response: str,
    intensity: str,
    turn_band: str,
    *,
    self_reference: re.Pattern[str],
    source: str,
    license_name: str,
    system_prompt: str = RU_SYSTEM_PROMPT,
) -> dict[str, object]:
    # _record — nguyên văn ru_v6_1_dialogues.py:339-363, thay _SELF_REFERENCE → self_reference, SOURCE → source, LICENSE → license_name, RU_SYSTEM_PROMPT → system_prompt
    safe_turns = [
        (role, self_reference.sub("anh", content) if role == "assistant" else content)
        for role, content in turns
    ]
    return {
        "messages": [
            {"role": "system", "content": system_prompt},
            *({"role": role, "content": content} for role, content in safe_turns),
        ],
        "response": self_reference.sub("anh", response),
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": source,
        "license": license_name,
    }


def final_prompt(
    language: str,
    mode: str,
    scene: Scene,
    index: int,
    *,
    vi_frames: tuple[str, ...],
    en_frames: tuple[str, ...],
    mixed_frames: tuple[str, ...],
    vi_tails: tuple[str, ...],
    en_tails: tuple[str, ...],
    mixed_tails: tuple[str, ...],
    identity_repair_en: str,
) -> str:
    # _final_prompt — nguyên văn ru_v6_1_dialogues.py:374-424, thay:
    #   _SCENES → SCENES, _VI_TIMES → VI_TIMES (3 chỗ), _VI_STANCES → VI_STANCES,
    #   _VI_FRAMES → vi_frames, _EN_FRAMES → en_frames, _MIXED_FRAMES → mixed_frames (đã lấy biến local vi_frame/en_frame/mixed_frame),
    #   _VI_TAILS → vi_tails, _EN_TAILS → en_tails, _MIXED_TAILS → mixed_tails,
    #   và dòng identity_repair/en: "please keep the conversation warm and consistent" → f"please keep the conversation {identity_repair_en} and consistent"
    time_index = (index * 5 + index // len(SCENES) * 3) % len(VI_TIMES)
    stance_index = (index * 7 + index // len(SCENES)) % len(VI_STANCES)
    vi_time, en_time, mixed_time = VI_TIMES[time_index], EN_TIMES[time_index], MIXED_TIMES[time_index]
    vi_stance, en_stance, mixed_stance = VI_STANCES[stance_index], EN_STANCES[stance_index], MIXED_STANCES[stance_index]
    frame_index = (index // len(SCENES) * 3 + index) % len(vi_frames)
    vi_frame, en_frame, mixed_frame = vi_frames[frame_index], en_frames[frame_index], mixed_frames[frame_index]
    tail_index = ((index // len(SCENES)) * 5 + index % len(SCENES)) % len(vi_tails)
    vi_tail, en_tail, mixed_tail = vi_tails[tail_index], en_tails[tail_index], mixed_tails[tail_index]

    if mode == "everyday_follow_up":
        if language == "vi":
            return f"Anh {scene.vi_event}; {vi_stance}, {vi_time}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I {scene.en_event}; {en_stance}, {en_time}; {en_frame}; {en_tail}."
        return f"Anh {scene.vi_event}, but {scene.en_feeling}; {mixed_time}, {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "story_context":
        if language == "vi":
            return f"Anh vẫn đang nghĩ về {scene.vi_noun} từ {vi_time}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I am still thinking about {scene.en_noun} from {en_time}; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh vẫn nghĩ về {scene.vi_noun} from {mixed_time}; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "emotion_support":
        if language == "vi":
            return f"Sau khi {scene.vi_event}, anh {scene.vi_feeling}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"After I {scene.en_event[5:] if scene.en_event.startswith('just ') else scene.en_event}, I {scene.en_feeling}; {en_stance}; {en_frame}; {en_tail}."
        return f"Sau khi anh {scene.vi_event}, I {scene.en_feeling}; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "language_switch":
        if language == "vi":
            return f"Anh muốn practice English một chút về việc anh {scene.vi_event}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I want to practice a Vietnamese sentence about how I {scene.en_event}; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh muốn switch language một chút: I {scene.en_event}; anh thấy {scene.vi_feeling}; {mixed_frame}; {mixed_tail}."
    if mode == "identity_repair":
        if language == "vi":
            return f"Lúc nãy em vừa xưng sai khi nói về {scene.vi_noun}; anh nhắc em giữ đúng `em` và gọi anh là `anh` nhé, {vi_frame}; {vi_tail}."
        if language == "en":
            return f"You used the wrong voice while we were talking about {scene.en_noun}; please keep the conversation {identity_repair_en} and consistent, {en_frame}; {en_tail}."
        return f"Lúc nãy em bị lệch voice khi nói về {scene.vi_noun}; please keep xưng hô là em với anh nhé, {mixed_frame}; {mixed_tail}."
    if mode == "humour":
        if language == "vi":
            return f"Anh {scene.vi_event} mà cảm giác như {scene.vi_noun} cũng đang chấm điểm kiên nhẫn của anh; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I {scene.en_event}, and it feels like {scene.en_noun} is quietly testing my patience; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh {scene.vi_event}, and somehow {scene.en_noun} is testing anh too; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if language == "vi":
        return f"Anh {scene.vi_event} rồi vẫn ghé qua tìm em; tối nay em nên làm anh cười bằng cách nào, {vi_frame.lower()} và {vi_tail.lower()}?"
    if language == "en":
        return f"I {scene.en_event} and still came by to talk to you; how should I make the evening a little warmer, {en_frame.lower()} and {en_tail.lower()}?"
    return f"Anh {scene.vi_event} rồi vẫn ghé qua đây; how can em make the evening softer for anh, {mixed_frame.lower()} and {mixed_tail.lower()}?"


def history_user(language: str, scene: Scene, step: int, index: int) -> str:
    # _history_user — nguyên văn ru_v6_1_dialogues.py:427-453
    if language == "vi":
        prompts = (
            f"Anh đang để ý đến {scene.vi_noun}.",
            f"Anh kể em nghe thêm một chút về {scene.vi_noun} nhé.",
            f"Lúc đó anh thấy {scene.vi_feeling}.",
            f"Anh chưa biết nên nhìn chuyện này thế nào.",
            f"Có lẽ anh muốn chậm lại với chuyện {scene.vi_noun}.",
        )
        return prompts[(step + index) % len(prompts)]
    if language == "en":
        prompts = (
            f"I keep thinking about {scene.en_noun}.",
            f"Let me tell you a little more about {scene.en_noun}.",
            f"It made me {scene.en_feeling}.",
            "I am not sure how to look at this yet.",
            f"I probably need to slow down with {scene.en_noun}.",
        )
        return prompts[(step + index) % len(prompts)]
    prompts = (
        f"Anh đang nghĩ về {scene.vi_noun} a little more.",
        f"Let em tell anh thêm về {scene.en_noun} nhé.",
        f"Chuyện đó made anh {scene.en_feeling}.",
        "Anh chưa sure mình nên nhìn chuyện này thế nào.",
        f"Có lẽ anh cần slow down với {scene.vi_noun}.",
    )
    return prompts[(step + index) % len(prompts)]


def history_assistant(language: str, scene: Scene, step: int, index: int) -> str:
    # _history_assistant — nguyên văn ru_v6_1_dialogues.py:456-491
    if language == "vi":
        responses = (
            f"Em vẫn theo mạch anh kể. {scene.vi_noun.capitalize()} có vẻ quan trọng với anh; anh và em cứ đi từng ý một thôi.",
            f"Nghe anh nói thêm về {scene.vi_noun}, em thấy chuyện này còn một lớp khác. Anh muốn chạm vào phần nào trước?",
            f"Em nhớ chi tiết {scene.vi_noun}; nó làm câu chuyện có điểm tựa hơn. Anh cứ kể theo nhịp của anh.",
            f"Đoạn này chưa cần kết luận ngay. Em vẫn giữ mạch {scene.vi_noun} để không bỏ mất điều anh đang cảm nhận.",
            f"Em hiểu vì sao {scene.vi_noun} cứ quay lại trong câu chuyện. Nếu anh muốn, em có thể nghe tiếp mà không chen lời.",
            f"Chuyện {scene.vi_noun} đang nối với cảm giác anh vừa nói. Anh muốn đi sâu hơn hay đổi sang một góc nhẹ hơn?",
            f"Em nghe được cả phần anh chưa nói hết về {scene.vi_noun}. Cứ để ý nghĩ đi thêm một đoạn rồi chúng ta chọn hướng nhé.",
            f"Chi tiết {scene.vi_noun} làm em hiểu nhịp của câu chuyện hơn. Anh muốn giữ nó lại hay kể sang điều mới?",
        )
        return responses[(step * 3 + index) % len(responses)]
    if language == "en":
        responses = (
            f"I am following you. {scene.en_noun.capitalize()} seems to matter to you, so we can take it one thought at a time.",
            f"The detail about {scene.en_noun} gives us a place to continue. Which part should we stay with first?",
            f"I am keeping {scene.en_noun} in view; it gives the story a useful thread. Take your time telling it.",
            f"You do not need a conclusion yet. We can let the thought about {scene.en_noun} unfold a little more.",
            f"I can see why {scene.en_noun} keeps returning in what you are saying. I am here for the next part too.",
            f"The feeling you described connects with {scene.en_noun}. Would you rather explore it or shift to something lighter?",
            f"I hear the part you have not fully said about {scene.en_noun}. We can give it another moment before deciding.",
            f"Keeping {scene.en_noun} in the thread helps me follow you. Do you want to hold onto it or move somewhere new?",
        )
        return responses[(step * 3 + index) % len(responses)]
    responses = (
        f"Em vẫn follow mạch anh kể. {scene.vi_noun.capitalize()} seems important to anh, anh và em cứ đi từng ý một nhé.",
        f"The detail about {scene.en_noun} vẫn gives us a place to continue. Anh muốn stay with phần nào trước?",
        f"Em đang giữ {scene.vi_noun} in view; it gives câu chuyện a useful thread. Anh cứ kể theo nhịp của anh.",
        f"Chưa cần kết luận ngay. We can let the thought about {scene.en_noun} unfold thêm một chút.",
        f"Em hiểu why {scene.vi_noun} keeps returning trong điều anh kể. Em vẫn ở đây for the next part.",
        f"The feeling anh vừa nói connects với {scene.en_noun}. Anh muốn explore it hay đổi sang a lighter angle?",
        f"Em nghe được phần anh chưa nói hết về {scene.vi_noun}. We can give it thêm một moment before deciding.",
        f"Giữ {scene.en_noun} trong thread giúp em follow anh rõ hơn. Anh muốn hold onto it hay move somewhere new?",
    )
    return responses[(step * 3 + index) % len(responses)]


def build_slots() -> list[dict[str, str]]:
    # _build_slots — nguyên văn ru_v6_1_dialogues.py:671-698, thay _LANGUAGE_MODE_COUNTS → LANGUAGE_MODE_COUNTS
    slots: list[dict[str, str]] = []
    for mode, language_counts in LANGUAGE_MODE_COUNTS.items():
        for language, count in language_counts.items():
            slots.extend({"language": language, "mode": mode} for _ in range(count))

    random = Random(42)
    random.shuffle(slots)

    flirt_slots = [index for index, slot in enumerate(slots) if slot["mode"] == "light_flirt"]
    for index in flirt_slots[:72]:
        slots[index]["intensity"] = "adult_boundary"
    for index in flirt_slots[72:]:
        slots[index]["intensity"] = "light"

    non_flirt = [index for index, slot in enumerate(slots) if slot["mode"] != "light_flirt"]
    for index in non_flirt[:240]:
        slots[index]["intensity"] = "light"
    for slot in slots:
        slot.setdefault("intensity", "neutral")

    bands = ["short"] * 480 + ["medium"] * 1200 + ["long"] * 720
    random.shuffle(bands)
    for slot, band in zip(slots, bands):
        slot["turn_band"] = band
    return slots


def build_rows(
    *,
    vi_frames: tuple[str, ...],
    en_frames: tuple[str, ...],
    mixed_frames: tuple[str, ...],
    vi_tails: tuple[str, ...],
    en_tails: tuple[str, ...],
    mixed_tails: tuple[str, ...],
    response_fn,
    self_reference: re.Pattern[str],
    source: str,
    license_name: str,
    identity_repair_en: str,
) -> list[dict[str, object]]:
    # _build_rows — nguyên văn ru_v6_1_dialogues.py:701-717, thay:
    #   _SCENES → SCENES, _build_slots() → build_slots(), _turn_count → turn_count,
    #   _history_user → history_user, _history_assistant → history_assistant,
    #   _final_prompt → final_prompt (kèm kwargs frames/tails/identity_repair_en),
    #   _response → response_fn, _record → record (kèm kwargs)
    rows: list[dict[str, object]] = []
    for index, slot in enumerate(build_slots()):
        scene = SCENES[index % len(SCENES)]
        turn_band = slot["turn_band"]
        turns = []
        history_turns = turn_count(turn_band, index) - 1
        for step in range(history_turns):
            turns.append(("user", history_user(slot["language"], scene, step, index)))
            turns.append(("assistant", history_assistant(slot["language"], scene, step, index)))
        prompt = final_prompt(
            slot["language"],
            slot["mode"],
            scene,
            index,
            vi_frames=vi_frames,
            en_frames=en_frames,
            mixed_frames=mixed_frames,
            vi_tails=vi_tails,
            en_tails=en_tails,
            mixed_tails=mixed_tails,
            identity_repair_en=identity_repair_en,
        )
        turns.append(("user", prompt))
        response = response_fn(
            slot["language"], slot["mode"], scene, index, slot["intensity"], turn_band
        )
        rows.append(
            record(
                slot["language"],
                slot["mode"],
                turns,
                response,
                slot["intensity"],
                turn_band,
                self_reference=self_reference,
                source=source,
                license_name=license_name,
            )
        )
    return rows
```

Thiếu import `Random` — thêm `from random import Random` vào đầu file (cùng nhóm import với `from dataclasses import dataclass`).

- [ ] **Step 2: Viết lại `src/mini_gpt/ru_v6_1_dialogues.py` (giữ tên hàm/hằng cũ)**

Nội dung mới:

```python
from __future__ import annotations

import re

from .corpus_common import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    VI_ACKS,
    VI_DETAILS,
    VI_QUESTIONS,
    EN_ACKS,
    EN_DETAILS,
    EN_QUESTIONS,
    SCENES,
    build_rows,
)
from .ru_style import RU_SYSTEM_PROMPT


SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
V6_1_TOTAL_ROWS = 2400

_SELF_REFERENCE = re.compile(r"(?i)(?<!một\s)(?<!\w)(?:tôi|tớ|mình)(?!\w)")

# _VI_FRAMES — nguyên văn ru_v6_1_dialogues.py:136-157 (20 items, item 11 "một câu nói dịu dàng")
_VI_FRAMES = (
    # ... 20 items copy nguyên văn từ file hiện tại ...
)

# _EN_FRAMES — nguyên văn ru_v6_1_dialogues.py:158-179 (item 11 "genuinely kind")
_EN_FRAMES = (
    # ... 20 items ...
)

# _MIXED_FRAMES — nguyên văn ru_v6_1_dialogues.py:180-201 (item 11 "một câu trả lời thật dịu dàng")
_MIXED_FRAMES = (
    # ... 20 items ...
)

# _VI_TAILS/_EN_TAILS/_MIXED_TAILS — nguyên văn ru_v6_1_dialogues.py:202-243 (item đầu "giữ lời kể nhẹ nhàng" / "stay close to the moment" / "keep câu chuyện gần với khoảnh khắc này")
_VI_TAILS = (
    # ... 12 items ...
)
_EN_TAILS = (
    # ... 12 items ...
)
_MIXED_TAILS = (
    # ... 12 items ...
)

# _MIXED_ACKS/_MIXED_DETAILS/_MIXED_QUESTIONS — nguyên văn ru_v6_1_dialogues.py:307-336 (viết Việt chủ đạo)
_MIXED_ACKS = (
    # ... 8 items ...
)
_MIXED_DETAILS = (
    # ... 8 items ...
)
_MIXED_QUESTIONS = (
    # ... 8 items ...
)


def _response_anchor(language: str, scene, index: int) -> str:
    # _response_anchor — nguyên văn ru_v6_1_dialogues.py:494-564 (dùng SCENES import từ common)
    start = (index // len(SCENES)) % 10
    close = (index // len(SCENES) * 3) % 7
    # ... toàn bộ 3 nhánh vi/en/mixed giữ nguyên văn ...


def _context_prefix(language: str, scene, index: int) -> str:
    # _context_prefix — nguyên văn ru_v6_1_dialogues.py:567-590
    variant = (index // len(SCENES)) % 4
    # ... giữ nguyên văn ...


def _finish_response(text: str) -> str:
    # _finish_response — nguyên văn ru_v6_1_dialogues.py:593-595
    text = re.sub(r"\.{2,}", ".", " ".join(text.split()))
    return text if text.endswith((".", "!", "?")) else f"{text}."


def _response(language: str, mode: str, scene, index: int, intensity: str, turn_band: str) -> str:
    # _response v6.1 — nguyên văn ru_v6_1_dialogues.py:598-668, thay:
    #   _VI_ACKS → VI_ACKS, _VI_DETAILS → VI_DETAILS, _VI_QUESTIONS → VI_QUESTIONS,
    #   _EN_ACKS → EN_ACKS, _EN_DETAILS → EN_DETAILS, _EN_QUESTIONS → EN_QUESTIONS
    #   (các _MIXED_* giữ local)
    # ... giữ nguyên văn toàn bộ 3 nhánh vi/en/mixed ...


V6_1_ROWS = build_rows(
    vi_frames=_VI_FRAMES,
    en_frames=_EN_FRAMES,
    mixed_frames=_MIXED_FRAMES,
    vi_tails=_VI_TAILS,
    en_tails=_EN_TAILS,
    mixed_tails=_MIXED_TAILS,
    response_fn=_response,
    self_reference=_SELF_REFERENCE,
    source=SOURCE,
    license_name=LICENSE,
    identity_repair_en="warm",
)


def v6_1_dialogue_records() -> list[dict[str, object]]:
    return [dict(row) for row in V6_1_ROWS]
```

BỎ các hằng này khỏi file (đã ở common): `EXPECTED_*` giờ import re-export từ common (dòng import ở trên đã gồm); `Scene`, `_SCENES`, `_VI_TIMES`, `_EN_TIMES`, `_MIXED_TIMES`, `_VI_STANCES`, `_EN_STANCES`, `_MIXED_STANCES`, `_record`, `_turn_count`, `_final_prompt`, `_history_user`, `_history_assistant`, `_build_slots`, `_build_rows` — XÓA khỏi v6_1_dialogues.py.
LƯU Ý: `ru_v6_1_corpus.py` import `EXPECTED_*` từ `ru_v6_1_dialogues` — vì dialogues giờ re-export từ common nên không vỡ.

- [ ] **Step 3: Verify — import + gate v6.1 + smoke audit**

Run: `.venv\Scripts\python.exe -c "from mini_gpt.ru_v6_1_dialogues import V6_1_ROWS, v6_1_dialogue_records; assert len(V6_1_ROWS) == 2400; assert len(v6_1_dialogue_records()) == 2400; print('OK')"`
Expected: `OK`
Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py --version v6_1`
Expected: `[PASS] v6_1` + `BYTE-IDENTICAL-OK`, exit 0. (Nếu FAIL: file dialogues đã bị sửa nội dung ở block nào đó — diff lại với git HEAD và sửa.)
Run: `.venv\Scripts\python.exe scripts\build_ru_llama_v6_1_data.py --audit` — smoke hành vi cũ
Expected: JSON report + held_out, exit 0.

- [ ] **Step 4: Chạy full suite**

Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 157 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/corpus_common.py src/mini_gpt/ru_v6_1_dialogues.py
git commit -m "refactor(corpus): extract shared v6.1/v6.2 machinery into corpus_common"
```

---

### Task 3: Chuyển `ru_v6_2_dialogues.py` sang dùng common machinery

**Files:**
- Modify: `src/mini_gpt/ru_v6_2_dialogues.py` (620 → ~280 dòng)
- Test: `tests/test_corpus_byte_identical.py` (gate v6.2)

**Interfaces:**
- Consumes: mọi thứ từ Task 2 (`build_rows`, `VI_ACKS`, ... `identity_repair_en="natural"`)

- [ ] **Step 1: Viết lại `src/mini_gpt/ru_v6_2_dialogues.py`**

```python
from __future__ import annotations

import re

from .corpus_common import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    VI_ACKS,
    VI_DETAILS,
    VI_QUESTIONS,
    EN_ACKS,
    EN_DETAILS,
    EN_QUESTIONS,
    build_rows,
)


SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
V6_2_TOTAL_ROWS = 2400

_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")

# _VI_FRAMES v6.2 — nguyên văn ru_v6_2_dialogues.py:136-157 (item 11 "một câu trả lời thật tự nhiên")
_VI_FRAMES = (
    # ... 20 items copy từ file hiện tại ...
)

# _EN_FRAMES v6.2 — nguyên văn ru_v6_2_dialogues.py:158-179 (item 11 "genuinely natural")
_EN_FRAMES = (
    # ... 20 items ...
)

# _MIXED_FRAMES v6.2 — nguyên văn ru_v6_2_dialogues.py:180-201 (item 11 "an answer thật natural")
_MIXED_FRAMES = (
    # ... 20 items ...
)

# _VI_TAILS v6.2 — nguyên văn ru_v6_2_dialogues.py:202-215 (item đầu "giữ lời kể thật tự nhiên")
_VI_TAILS = (
    # ... 12 items ...
)

# _EN_TAILS v6.2 — nguyên văn ru_v6_2_dialogues.py:216-229 (item đầu "stay natural")
_EN_TAILS = (
    # ... 12 items ...
)

# _MIXED_TAILS v6.2 — nguyên văn ru_v6_2_dialogues.py:230-243 (item đầu "keep the story natural")
_MIXED_TAILS = (
    # ... 12 items ...
)

# _MIXED_ACKS/_MIXED_DETAILS/_MIXED_QUESTIONS v6.2 — nguyên văn ru_v6_2_dialogues.py:307-336 (code-switching mạnh: "Em hiểu what anh means", ...)
_MIXED_ACKS = (
    # ... 8 items ...
)
_MIXED_DETAILS = (
    # ... 8 items ...
)
_MIXED_QUESTIONS = (
    # ... 8 items ...
)


def _response(language: str, mode: str, scene, index: int, intensity: str, turn_band: str) -> str:
    # _response v6.2 — nguyên văn ru_v6_2_dialogues.py:494-564, thay:
    #   _VI_ACKS → VI_ACKS, _VI_DETAILS → VI_DETAILS, _VI_QUESTIONS → VI_QUESTIONS,
    #   _EN_ACKS → EN_ACKS, _EN_DETAILS → EN_DETAILS, _EN_QUESTIONS → EN_QUESTIONS
    #   (KHÔNG có _response_anchor/_context_prefix/_finish_response — không dùng)
    # ... giữ nguyên văn ...


V6_2_ROWS = build_rows(
    vi_frames=_VI_FRAMES,
    en_frames=_EN_FRAMES,
    mixed_frames=_MIXED_FRAMES,
    vi_tails=_VI_TAILS,
    en_tails=_EN_TAILS,
    mixed_tails=_MIXED_TAILS,
    response_fn=_response,
    self_reference=_SELF_REFERENCE,
    source=SOURCE,
    license_name=LICENSE,
    identity_repair_en="natural",
)


def v6_2_dialogue_records() -> list[dict[str, object]]:
    return [dict(row) for row in V6_2_ROWS]
```

BỎ khỏi file: toàn bộ machinery đã ở common (giống Task 2) + `RU_SYSTEM_PROMPT` import (không dùng trực tiếp nữa — record nhận system_prompt default). Kiểm tra: file cũ import `RU_SYSTEM_PROMPT` ở line 7 nhưng sau refactor không còn dùng — XÓA import để tránh unused.

- [ ] **Step 2: Verify — import + gate v6.2 + smoke audit**

Run: `.venv\Scripts\python.exe -c "from mini_gpt.ru_v6_2_dialogues import V6_2_ROWS, v6_2_dialogue_records; assert len(V6_2_ROWS) == 2400; print('OK')"`
Expected: `OK`
Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py --version v6_2`
Expected: `[PASS] v6_2` + `BYTE-IDENTICAL-OK`, exit 0.
Run: `.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py --audit` — smoke hành vi cũ
Expected: JSON report + held_out, exit 0.

- [ ] **Step 3: Chạy full suite**

Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 157 passed, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add src/mini_gpt/ru_v6_2_dialogues.py
git commit -m "refactor(corpus): move v6.2 dialogues onto shared corpus_common machinery"
```

---

### Task 4: `corpus_validate.py` — parameterized validator + `ru_v6_1_corpus.py` thành wrapper

**Files:**
- Create: `src/mini_gpt/corpus_validate.py`
- Modify: `src/mini_gpt/ru_v6_1_corpus.py` (294 → ~55 dòng)
- Test: `tests/test_corpus_byte_identical.py` (gate v6.1), `tests/test_ru_hard_evaluation.py`

**Interfaces:**
- Consumes: `_base_corpus.*`, `_safety_patterns.*`, `_text_utils.normalize_text`, `_corpus_quality.casual_rejection_reason`, `ru_style.RU_SYSTEM_PROMPT`, `ru_v6_1_naturalness.{assert_authored_rows_are_natural, audit_rows, normalized_final_prompt}`, `ru_v6_1_dialogues.*` (qua wrapper)
- Produces:
  - `normalize_row(raw_row: dict, *, version: str, expected_fields: frozenset[str], valid_languages, valid_modes, valid_intensities, valid_turn_bands, source: str, license_name: str, system_prompt: str, allow_approved: bool, approved_source: str | None) -> dict`
  - `build_corpus(rows, *, output_dir, version, expected_total, expected_split_counts, expected_language_counts, expected_mode_counts, expected_intensity_counts, expected_turn_band_counts, valid_languages, valid_modes, valid_intensities, valid_turn_bands, source, license_name, system_prompt=RU_SYSTEM_PROMPT, allow_approved=False, approved_source=None, naturalness_gate=False, casefold_prompts=False, seed, write=True) -> dict[str, object]`

- [ ] **Step 1: Tạo `src/mini_gpt/corpus_validate.py`**

```python
from __future__ import annotations

from collections import Counter
from pathlib import Path
from random import Random

from ._base_corpus import (
    canonical_rows,
    is_contextual,
    pair_key,
    validate_alternating_roles,
    write_splits_atomic,
)
from ._corpus_quality import casual_rejection_reason
from ._safety_patterns import (
    AI_DISCLAIMER as _AI_DISCLAIMER_PATTERN,
    CONTEXT_STOPWORDS as _CONTEXT_STOPWORDS,
    CONTEXT_TOKEN as _CONTEXT_TOKEN_PATTERN,
    EN_SIGNAL as _EN_SIGNAL_PATTERN,
    GRAPHIC_CONTENT as _GRAPHIC_PATTERN,
    VI_WRONG_SELF as _SELF_REFERENCE_PATTERN,
    UNSAFE_CONTENT as _UNSAFE_PATTERN,
    VI_SIGNAL as _VI_SIGNAL_PATTERN,
)
from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_1_naturalness import (
    assert_authored_rows_are_natural,
    audit_rows,
    normalized_final_prompt,
)


def _context_tokens(text: str) -> set[str]:
    return {
        token.casefold()
        for token in _CONTEXT_TOKEN_PATTERN.findall(text)
        if token.casefold() not in _CONTEXT_STOPWORDS
    }


def _has_context_signal(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    history = " ".join(str(message["content"]) for message in messages[1:-1])
    response = str(row["response"])
    return bool(_context_tokens(history) & _context_tokens(response))


def _expected_turn_ranges() -> dict[str, range]:
    return {
        "short": range(2, 4),
        "medium": range(4, 9),
        "long": range(9, 15),
    }


def normalize_row(
    raw_row: dict[str, object],
    *,
    version: str,
    expected_fields: frozenset[str],
    valid_languages: frozenset[str],
    valid_modes: frozenset[str],
    valid_intensities: frozenset[str],
    valid_turn_bands: frozenset[str],
    source: str,
    license_name: str,
    system_prompt: str,
    allow_approved: bool = False,
    approved_source: str | None = None,
) -> dict[str, object]:
    is_approved = "approval_id" in raw_row
    if is_approved and not allow_approved:
        raise ValueError(f"{version} authored record has unexpected fields")
    if set(raw_row) != (expected_fields | ({"approval_id"} if is_approved else set())):
        raise ValueError(f"{version} authored record has unexpected fields")

    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError(f"{version} authored record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError(f"{version} message must contain exactly role and content")
        role = raw_message["role"]
        content = raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError(f"{version} message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError(f"{version} message content cannot be empty")
        messages.append({"role": role, "content": content})

    if messages[0] != {"role": "system", "content": system_prompt}:
        raise ValueError(f"{version} record must start with the Ru system prompt")
    validate_alternating_roles(messages, version)

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    intensity = raw_row["intensity"]
    turn_band = raw_row["turn_band"]
    source_value = raw_row["source"]
    row_license = raw_row["license"]
    values = (response, language, mode, intensity, turn_band, source_value, row_license)
    if not all(isinstance(value, str) for value in values):
        raise ValueError(f"{version} response metadata must be strings")

    response = normalize_text(response)
    if language not in valid_languages:
        raise ValueError(f"{version} record has an unsupported language")
    if mode not in valid_modes:
        raise ValueError(f"{version} record has an unsupported mode")
    if intensity not in valid_intensities:
        raise ValueError(f"{version} record has an unsupported intensity")
    if turn_band not in valid_turn_bands:
        raise ValueError(f"{version} record has an unsupported turn band")
    if not (source_value == source or (is_approved and source_value == approved_source)) or row_license != license_name:
        raise ValueError(f"{version} record provenance is invalid")

    if is_approved:
        approval_id = raw_row["approval_id"]
        if not isinstance(approval_id, str) or not approval_id:
            raise ValueError(f"{version} approved record has an invalid approval id")
        return {
            "messages": messages,
            "response": response,
            "language": language,
            "mode": mode,
            "intensity": intensity,
            "turn_band": turn_band,
            "source": source_value,
            "license": row_license,
            "approval_id": approval_id,
        }

    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"{version} authored record failed quality gate: {reason}")
    if _AI_DISCLAIMER_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: ai_disclaimer")
    if _SELF_REFERENCE_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_voice")
    if _GRAPHIC_PATTERN.search(response) or _UNSAFE_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: explicit_or_unsafe_content")

    assistant_messages = [message["content"] for message in messages[1:-1] if message["role"] == "assistant"]
    if any(_SELF_REFERENCE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_voice_history")
    if any(_GRAPHIC_PATTERN.search(content) or _UNSAFE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{version} authored record failed quality gate: unsafe_history")

    user_turn_count = sum(message["role"] == "user" for message in messages[1:])
    if user_turn_count not in _expected_turn_ranges()[turn_band]:
        raise ValueError(f"{version} record turn count does not match its turn band")
    if turn_band == "long" and not _has_context_signal({"messages": messages, "response": response}):
        raise ValueError(f"{version} authored record failed quality gate: long_context_missing")

    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: english_signal_missing")
    if language == "mixed" and (
        not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response)
    ):
        raise ValueError(f"{version} authored record failed quality gate: mixed_language")
    if language == "vi" and not _VI_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_signal_missing")

    return {
        "messages": messages,
        "response": response,
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": source_value,
        "license": row_license,
    }


def build_corpus(
    rows: list[dict[str, object]],
    *,
    output_dir: str | Path,
    version: str,
    expected_total: int,
    expected_split_counts: dict[str, int],
    expected_language_counts: dict[str, int],
    expected_mode_counts: dict[str, int],
    expected_intensity_counts: dict[str, int],
    expected_turn_band_counts: dict[str, int],
    valid_languages: frozenset[str],
    valid_modes: frozenset[str],
    valid_intensities: frozenset[str],
    valid_turn_bands: frozenset[str],
    source: str,
    license_name: str,
    system_prompt: str = RU_SYSTEM_PROMPT,
    allow_approved: bool = False,
    approved_source: str | None = None,
    naturalness_gate: bool = False,
    casefold_prompts: bool = False,
    seed: int,
    write: bool = True,
) -> dict[str, object]:
    normalized_rows = [
        normalize_row(
            row,
            version=version,
            expected_fields=frozenset(
                {"messages", "response", "language", "mode", "intensity", "turn_band", "source", "license"}
            ),
            valid_languages=valid_languages,
            valid_modes=valid_modes,
            valid_intensities=valid_intensities,
            valid_turn_bands=valid_turn_bands,
            source=source,
            license_name=license_name,
            system_prompt=system_prompt,
            allow_approved=allow_approved,
            approved_source=approved_source,
        )
        for row in rows
    ]
    if len(normalized_rows) != expected_total:
        raise ValueError(f"{version} corpus must contain exactly {expected_total} rows")
    if naturalness_gate:
        assert_authored_rows_are_natural(normalized_rows)

    pair_keys = [pair_key(row) for row in normalized_rows]
    if len(pair_keys) != len(set(pair_keys)):
        raise ValueError(f"{version} corpus contains exact duplicate prompt/response pairs")
    if casefold_prompts:
        prompts = [normalized_final_prompt(row) for row in normalized_rows]
    else:
        prompts = [str(row["messages"][-1]["content"]) for row in normalized_rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError(f"{version} corpus contains duplicate final prompts")

    language_counts = dict(Counter(str(row["language"]) for row in normalized_rows))
    if language_counts != expected_language_counts:
        raise ValueError(f"{version} language counts do not match the approved shape: {language_counts}")
    mode_counts = dict(Counter(str(row["mode"]) for row in normalized_rows))
    if mode_counts != expected_mode_counts:
        raise ValueError(f"{version} mode counts do not match the approved shape: {mode_counts}")
    intensity_counts = dict(Counter(str(row["intensity"]) for row in normalized_rows))
    if intensity_counts != expected_intensity_counts:
        raise ValueError(f"{version} intensity counts do not match the approved shape: {intensity_counts}")
    turn_band_counts = dict(Counter(str(row["turn_band"]) for row in normalized_rows))
    if turn_band_counts != expected_turn_band_counts:
        raise ValueError(f"{version} turn-band counts do not match the approved shape: {turn_band_counts}")

    contextual_count = sum(is_contextual(row, check_history_only=True) for row in normalized_rows)
    if contextual_count < 1200:
        raise ValueError(f"{version} corpus needs at least 1200 contextual rows, found {contextual_count}")
    identity_repair_count = sum(str(row["mode"]) == "identity_repair" for row in normalized_rows)
    if identity_repair_count != expected_mode_counts["identity_repair"]:
        raise ValueError(f"{version} identity-repair count does not match the approved shape")
    long_context_count = sum(
        str(row["turn_band"]) == "long" and _has_context_signal(row) for row in normalized_rows
    )
    if long_context_count != expected_turn_band_counts["long"]:
        raise ValueError(f"{version} long rows must all retain an earlier context signal")

    shuffled_indices = list(range(len(normalized_rows)))
    Random(seed).shuffle(shuffled_indices)
    held_out_end = expected_split_counts["held_out"]
    validation_end = held_out_end + expected_split_counts["validation"]
    held_out = [normalized_rows[index] for index in shuffled_indices[:held_out_end]]
    validation = [normalized_rows[index] for index in shuffled_indices[held_out_end:validation_end]]
    train = [normalized_rows[index] for index in shuffled_indices[validation_end:]]
    splits = {"train": train, "validation": validation, "held_out": held_out}

    split_counts = {name: len(rows) for name, rows in splits.items()}
    if split_counts != expected_split_counts:
        raise ValueError(f"{version} split counts do not match the approved shape: {split_counts}")
    split_prompt_sets = {
        name: {prompt for prompt in map(lambda row: normalized_final_prompt(row) if casefold_prompts else str(row["messages"][-1]["content"]), rows)}
        for name, rows in splits.items()
    }
    if any(
        split_prompt_sets[left] & split_prompt_sets[right]
        for left, right in (("train", "validation"), ("train", "held_out"), ("validation", "held_out"))
    ):
        raise ValueError(f"{version} final prompts overlap between splits")

    report: dict[str, object] = {
        "total_examples": len(normalized_rows),
        "split_counts": split_counts,
        "language_counts": language_counts,
        "mode_counts": mode_counts,
        "intensity_counts": intensity_counts,
        "turn_band_counts": turn_band_counts,
        "contextual_examples": contextual_count,
        "identity_repair_examples": identity_repair_count,
        "long_context_examples": long_context_count,
        "prompt_group_count": len(set(prompts)),
        "source_counts": dict(Counter(str(row["source"]) for row in normalized_rows)),
        "licenses": dict(Counter(str(row["license"]) for row in normalized_rows)),
        "source_sha256": canonical_rows(normalized_rows),
        "train_sha256": canonical_rows(train),
        "validation_sha256": canonical_rows(validation),
        "held_out_sha256": canonical_rows(held_out),
        "seed": seed,
        "quality_gates": {
            "rejects_links_code_markup_lists_ai_disclaimers": True,
            "rejects_vietnamese_self_reference": ["tôi", "tớ", "mình"],
            "rejects_graphic_or_unsafe_assistant_content": True,
            "requires_alternating_contextual_turns": True,
        },
    }
    if naturalness_gate:
        report["naturalness_audit"] = audit_rows(normalized_rows)["summary"]

    if not write:
        return report

    write_splits_atomic(Path(output_dir), train, validation, held_out, report)
    return report
```

LƯU Ý KHỚP HÀNH VI:
- v6.1 cũ: prompt-duplicate check dùng `normalized_final_prompt` → `casefold_prompts=True`; split-overlap check v6.1 cũng dùng `normalized_final_prompt`. ✓ theo code trên.
- v6.2 cũ: dùng `str(row["messages"][-1]["content"])` (KHÔNG normalize) → `casefold_prompts=False`. ✓.
- v6.1 cũ: `VALID_MODES` có `user_approved_short_reply`, `VALID_TURN_BANDS` có `single_turn` — truyền qua wrapper.
- `_expected_turn_ranges()` trả range MỚI mỗi lần gọi (range objects reusable). ✓.

- [ ] **Step 2: Viết lại `src/mini_gpt/ru_v6_1_corpus.py` thành wrapper**

```python
from __future__ import annotations

from pathlib import Path

from .corpus_validate import build_corpus
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_1_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_1_ROWS,
    V6_1_TOTAL_ROWS,
)

VERSION = "v6.1"
EXPECTED_TOTAL = V6_1_TOTAL_ROWS
EXPECTED_SPLIT_COUNTS = {"train": 1680, "validation": 360, "held_out": 360}
EXPECTED_LANGUAGE_COUNTS = dict(EXPECTED_LANGUAGE_COUNTS)
EXPECTED_MODE_COUNTS = dict(EXPECTED_MODE_COUNTS)
EXPECTED_INTENSITY_COUNTS = dict(EXPECTED_INTENSITY_COUNTS)
EXPECTED_TURN_BAND_COUNTS = dict(EXPECTED_TURN_BAND_COUNTS)

VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS) | {"user_approved_short_reply"}
VALID_INTENSITIES = frozenset(EXPECTED_INTENSITY_COUNTS)
VALID_TURN_BANDS = frozenset(EXPECTED_TURN_BAND_COUNTS) | {"single_turn"}
APPROVED_SOURCE = "user-approved-local"


def build_ru_v6_1_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    return build_corpus(
        V6_1_ROWS,
        output_dir=output_dir,
        version="v6.1",
        expected_total=EXPECTED_TOTAL,
        expected_split_counts=EXPECTED_SPLIT_COUNTS,
        expected_language_counts=EXPECTED_LANGUAGE_COUNTS,
        expected_mode_counts=EXPECTED_MODE_COUNTS,
        expected_intensity_counts=EXPECTED_INTENSITY_COUNTS,
        expected_turn_band_counts=EXPECTED_TURN_BAND_COUNTS,
        valid_languages=VALID_LANGUAGES,
        valid_modes=VALID_MODES,
        valid_intensities=VALID_INTENSITIES,
        valid_turn_bands=VALID_TURN_BANDS,
        source=SOURCE,
        license_name=LICENSE,
        system_prompt=RU_SYSTEM_PROMPT,
        allow_approved=True,
        approved_source=APPROVED_SOURCE,
        naturalness_gate=True,
        casefold_prompts=True,
        seed=seed,
        write=write,
    )
```

XÓA khỏi ru_v6_1_corpus.py: toàn bộ `_context_tokens`/`_has_context_signal`/`_normalize_row`/`build_ru_v6_1_corpus` cũ, các import `_safety_patterns`, `_text_utils`, `_corpus_quality`, `_base_corpus` (trừ `Path`), `ru_v6_1_naturalness`.
Giữ: toàn bộ constants exports + `build_ru_v6_1_corpus` signature mới (giống hệt cũ).

- [ ] **Step 3: Verify — gate v6.1 + approved path + smoke audit**

Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py --version v6_1`
Expected: `[PASS] v6_1`, exit 0. (Manifest v6.1 phải khớp — gồm key `naturalness_audit` ở cuối.)
Run: `.venv\Scripts\python.exe scripts\build_ru_llama_v6_1_data.py --audit` — smoke
Expected: report có `naturalness_audit`, exit 0.
Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 157 passed.

- [ ] **Step 4: Commit**

```bash
git add src/mini_gpt/corpus_validate.py src/mini_gpt/ru_v6_1_corpus.py
git commit -m "refactor(corpus): unify v6.1/v6.2 validators into parameterized corpus_validate"
```

---

### Task 5: `ru_v6_2_corpus.py` thành wrapper

**Files:**
- Modify: `src/mini_gpt/ru_v6_2_corpus.py` (279 → ~50 dòng)
- Test: `tests/test_corpus_byte_identical.py` (gate v6.2)

**Interfaces:**
- Consumes: `corpus_validate.build_corpus` (Task 4), `ru_v6_2_dialogues.*`

- [ ] **Step 1: Viết lại `src/mini_gpt/ru_v6_2_corpus.py` thành wrapper**

```python
from __future__ import annotations

from pathlib import Path

from .corpus_validate import build_corpus
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_2_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_2_ROWS,
    V6_2_TOTAL_ROWS,
)


EXPECTED_TOTAL = V6_2_TOTAL_ROWS
EXPECTED_SPLIT_COUNTS = {"train": 1680, "validation": 360, "held_out": 360}
EXPECTED_LANGUAGE_COUNTS = dict(EXPECTED_LANGUAGE_COUNTS)
EXPECTED_MODE_COUNTS = dict(EXPECTED_MODE_COUNTS)
EXPECTED_INTENSITY_COUNTS = dict(EXPECTED_INTENSITY_COUNTS)
EXPECTED_TURN_BAND_COUNTS = dict(EXPECTED_TURN_BAND_COUNTS)

VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS)
VALID_INTENSITIES = frozenset(EXPECTED_INTENSITY_COUNTS)
VALID_TURN_BANDS = frozenset(EXPECTED_TURN_BAND_COUNTS)


def build_ru_v6_2_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    return build_corpus(
        V6_2_ROWS,
        output_dir=output_dir,
        version="v6.2",
        expected_total=EXPECTED_TOTAL,
        expected_split_counts=EXPECTED_SPLIT_COUNTS,
        expected_language_counts=EXPECTED_LANGUAGE_COUNTS,
        expected_mode_counts=EXPECTED_MODE_COUNTS,
        expected_intensity_counts=EXPECTED_INTENSITY_COUNTS,
        expected_turn_band_counts=EXPECTED_TURN_BAND_COUNTS,
        valid_languages=VALID_LANGUAGES,
        valid_modes=VALID_MODES,
        valid_intensities=VALID_INTENSITIES,
        valid_turn_bands=VALID_TURN_BANDS,
        source=SOURCE,
        license_name=LICENSE,
        system_prompt=RU_SYSTEM_PROMPT,
        allow_approved=False,
        approved_source=None,
        naturalness_gate=False,
        casefold_prompts=False,
        seed=seed,
        write=write,
    )
```

LƯU Ý: `ru_v6_2_corpus.py` file cũ KHÔNG có `VERSION`/`APPROVED_SOURCE` — giữ nguyên không thêm.

- [ ] **Step 2: Verify — gate v6.2 + full suite**

Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py --version v6_2`
Expected: `[PASS] v6_2`, exit 0.
Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 157 passed.

- [ ] **Step 3: Commit**

```bash
git add src/mini_gpt/ru_v6_2_corpus.py
git commit -m "refactor(corpus): turn v6.2 corpus builder into thin wrapper over corpus_validate"
```

---

### Task 6: CLI hợp nhất `build_corpus.py` + xóa 5 script cũ + cập nhật tài liệu

**Files:**
- Create: `scripts/build_corpus.py`
- Delete: `scripts/build_ru_llama_v6_data.py`, `scripts/build_ru_llama_v6_1_data.py`, `scripts/build_ru_llama_v6_2_data.py`, `scripts/build_ru_llama_v6_2_fresh_data.py`, `scripts/build_ru_llama_v6_3_data.py`
- Modify: `README.md:165,188`, `docs/codebase-review-fixes.md`
- Test: `tests/test_build_corpus_cli.py` (mới)

**Interfaces:**
- Consumes: `build_ru_v6_corpus`, `v6_dialogue_records`, `build_ru_v6_1_corpus`, `build_ru_v6_2_corpus`, `build_ru_v6_2_fresh_corpus`, `build_ru_v6_3_corpus` (signatures giữ nguyên từ trước)
- Produces: `build_corpus.py --version {v6,v6_1,v6_2,v6_2_fresh,v6_3}` CLI

- [ ] **Step 1: Tạo `scripts/build_corpus.py`**

```python
from __future__ import annotations

import argparse
import json
import sys
import warnings
from pathlib import Path
from tempfile import TemporaryDirectory

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v6_corpus import build_ru_v6_corpus
from mini_gpt.ru_v6_dialogues import v6_dialogue_records
from mini_gpt.ru_v6_1_corpus import build_ru_v6_1_corpus
from mini_gpt.ru_v6_2_corpus import build_ru_v6_2_corpus
from mini_gpt.ru_v6_2_fresh_corpus import build_ru_v6_2_fresh_corpus
from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus

VERSION_DEFAULTS = {
    "v6": {"dir": "ru_v6", "seed": 42, "deprecated": True},
    "v6_1": {"dir": "ru_v6_1", "seed": 42},
    "v6_2": {"dir": "ru_v6_2", "seed": 42},
    "v6_2_fresh": {"dir": "ru_v6_2_fresh", "seed": 62},
    "v6_3": {"dir": "ru_v6_3", "seed": 63},
}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build any Ru corpus version from a single CLI")
    parser.add_argument("--version", required=True, choices=sorted(VERSION_DEFAULTS))
    parser.add_argument("--output-dir", type=Path, default=None)
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--audit", action="store_true", help="v6/v6.1/v6.2: build to a temp dir and dump held-out rows")
    parser.add_argument("--audit-only", action="store_true", help="v6.2_fresh/v6.3: validate without writing")
    parser.add_argument(
        "--approved-file",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "approved.jsonl",
    )
    parser.add_argument(
        "--memory-file",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "memory_texts.json",
    )
    return parser


def _rows(path: Path) -> list[dict[str, object]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def _dump(report: dict[str, object], held_out: list[dict[str, object]] | None = None) -> None:
    payload: dict[str, object] = {"report": report}
    if held_out is not None:
        payload["held_out"] = held_out
    print(json.dumps(payload, ensure_ascii=False, indent=2))


def _run_v6(args: argparse.Namespace) -> int:
    if args.audit_only:
        raise SystemExit("--audit-only is not supported for v6; use --audit")
    warnings.warn("V6 corpus is deprecated; use v6.1+ corpora instead", DeprecationWarning, stacklevel=2)
    records = v6_dialogue_records()
    if args.audit:
        with TemporaryDirectory(prefix="ru-v6-audit-") as temporary_dir:
            report = build_ru_v6_corpus(records, temporary_dir, seed=args.seed)
            held_out = _rows(Path(temporary_dir) / "held_out.jsonl")
        _dump(report, held_out)
        return 0
    _dump(build_ru_v6_corpus(records, args.output_dir, seed=args.seed))
    return 0


def _run_v6_1_2(args: argparse.Namespace) -> int:
    if args.audit_only:
        raise SystemExit(f"--audit-only is not supported for {args.version}; use --audit")
    builder = build_ru_v6_1_corpus if args.version == "v6_1" else build_ru_v6_2_corpus
    if args.audit:
        with TemporaryDirectory(prefix=f"ru-{args.version}-audit-") as temporary_dir:
            report = builder(temporary_dir, seed=args.seed)
            held_out = _rows(Path(temporary_dir) / "held_out.jsonl")
        _dump(report, held_out)
        return 0
    _dump(builder(args.output_dir, seed=args.seed))
    return 0


def _run_fresh(args: argparse.Namespace) -> int:
    if not args.audit_only:
        for path, label in ((args.approved_file, "approved-file"), (args.memory_file, "memory-file")):
            if not path.is_file():
                raise SystemExit(f"{label} not found: {path}")
    _dump(
        build_ru_v6_2_fresh_corpus(
            args.output_dir,
            approved_file=args.approved_file,
            memory_file=args.memory_file,
            seed=args.seed,
            write=not args.audit_only,
        )
    )
    return 0


def _run_v6_3(args: argparse.Namespace) -> int:
    _dump(build_ru_v6_3_corpus(args.output_dir, seed=args.seed, write=not args.audit_only))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    defaults = VERSION_DEFAULTS[args.version]
    args.output_dir = args.output_dir or (PROJECT_ROOT / "data" / "conversations" / defaults["dir"])
    args.seed = args.seed if args.seed is not None else defaults["seed"]

    if args.version == "v6":
        return _run_v6(args)
    if args.version in {"v6_1", "v6_2"}:
        return _run_v6_1_2(args)
    if args.version == "v6_2_fresh":
        return _run_fresh(args)
    return _run_v6_3(args)


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 2: Tạo `tests/test_build_corpus_cli.py`**

```python
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON = sys.executable
BUILD_CLI = "scripts/build_corpus.py"


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [PYTHON, BUILD_CLI, *args],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def test_cli_requires_version() -> None:
    result = _run()
    assert result.returncode == 2
    assert "--version" in result.stderr


def test_cli_unknown_version_rejected() -> None:
    result = _run("--version", "v6_9")
    assert result.returncode == 2


def test_cli_v6_3_audit_only_writes_nothing(tmp_path: Path) -> None:
    result = _run("--version", "v6_3", "--audit-only", "--output-dir", str(tmp_path))
    assert result.returncode == 0
    payload = json.loads(result.stdout)
    assert payload["report"]["authored_total_examples"] == 180
    assert not (tmp_path / "train.jsonl").exists()


def test_cli_v6_1_audit_dumps_held_out(tmp_path: Path) -> None:
    result = _run("--version", "v6_1", "--audit")
    assert result.returncode == 0
    payload = json.loads(result.stdout)
    assert payload["report"]["total_examples"] == 2400
    assert len(payload["held_out"]) == 360


def test_cli_fresh_missing_files_exits_1(tmp_path: Path) -> None:
    result = _run(
        "--version",
        "v6_2_fresh",
        "--approved-file",
        str(tmp_path / "missing-approved.jsonl"),
        "--memory-file",
        str(tmp_path / "missing-memory.json"),
        "--audit-only",
    )
    assert result.returncode == 0  # audit-only bỏ qua check file
    result = _run(
        "--version",
        "v6_2_fresh",
        "--approved-file",
        str(tmp_path / "missing-approved.jsonl"),
        "--memory-file",
        str(tmp_path / "missing-memory.json"),
    )
    assert result.returncode == 1
    assert "approved-file not found" in result.stderr
```

- [ ] **Step 3: Xóa 5 script cũ**

```bash
git rm scripts/build_ru_llama_v6_data.py scripts/build_ru_llama_v6_1_data.py scripts/build_ru_llama_v6_2_data.py scripts/build_ru_llama_v6_2_fresh_data.py scripts/build_ru_llama_v6_3_data.py
```

- [ ] **Step 4: Cập nhật tài liệu**

README.md:165 — đổi:
`& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_1_data.py --output-dir data\conversations\ru_v6_1 --seed 42`
thành:
`& D:\LLM\.venv\Scripts\python.exe scripts\build_corpus.py --version v6_1 --output-dir data\conversations\ru_v6_1 --seed 42`

README.md:188 — đổi:
`& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_fresh_data.py`
thành:
`& D:\LLM\.venv\Scripts\python.exe scripts\build_corpus.py --version v6_2_fresh`

docs/codebase-review-fixes.md — thêm mục cuối (sau dòng 222):
```markdown
## Round 4 (2026-08-01): Corpus pipeline refactor

- `scripts/build_corpus.py` — CLI hợp nhất build mọi version (`--version {v6,v6_1,v6_2,v6_2_fresh,v6_3}`), thay thế 5 script `build_ru_llama_v6*_data.py` (đã xóa).
- `src/mini_gpt/corpus_common.py` — machinery dùng chung v6.1/v6.2 (Scene/SCENES, times, stances, acks/details/questions, slots, record, history, final_prompt).
- `src/mini_gpt/corpus_validate.py` — validator tham số hóa thay `_normalize_row`/`build_*_corpus` trùng ~90% của v6.1/v6.2; hai file corpus thành thin wrappers.
- `scripts/verify_corpus_byte_identical.py` — gate SHA256 toàn bộ version so data hiện có (byte-for-byte).
```

- [ ] **Step 5: Verify**

Run: `.venv\Scripts\python.exe -m pytest tests\test_build_corpus_cli.py -v --tb=short`
Expected: 5 passed.
Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6_2_fresh --audit-only` — smoke fresh
Expected: JSON report, exit 0.
Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py`
Expected: toàn bộ 5 version PASS.
Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 162 passed (157 + 5 CLI), 0 failed.

- [ ] **Step 6: Commit**

```bash
git add scripts/build_corpus.py tests/test_build_corpus_cli.py README.md docs/codebase-review-fixes.md
git commit -m "refactor(corpus): replace five per-version build scripts with single build_corpus CLI"
```

---

### Task 7: Full verification + báo cáo giảm dòng

**Files:**
- Test: toàn bộ suite + gate + compileall

- [ ] **Step 1: Gate toàn bộ + suite đầy đủ**

Run: `.venv\Scripts\python.exe scripts\verify_corpus_byte_identical.py`
Expected: 4× `[PASS]` (v6_1, v6_2, v6_2_fresh, v6_3 — v6 không nằm trong gate), `REFACTOR-NEUTRAL-OK`, exit 0.
Run: `.venv\Scripts\python.exe -m pytest tests\ -q --tb=short`
Expected: 161 passed, 0 failed, 2 deprecation warnings (aifc, pkg_resources — từ thư viện, không phải lỗi).
Run: `.venv\Scripts\python.exe -m compileall -q src\mini_gpt scripts`
Expected: không output, exit 0.

- [ ] **Step 2: Smoke mọi version qua CLI mới (không ghi data)**

Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6 --audit 2>$null` — expected exit 0
Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6_1 --audit` — expected exit 0
Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6_2 --audit` — expected exit 0
Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6_2_fresh --audit-only` — expected exit 0
Run: `.venv\Scripts\python.exe scripts\build_corpus.py --version v6_3 --audit-only` — expected exit 0

- [ ] **Step 3: Đo số dòng giảm**

Run: `git diff --stat HEAD~6` (hoặc cộng dòng: trước = 724+620+294+279+47+43+43+42+30 = 2122; sau = corpus_common ~330 + v6_1_dialogues ~330 + v6_2_dialogues ~280 + corpus_validate ~310 + 2 wrapper ~105 + build_corpus ~150 ≈ 1505 → giảm ~600 dòng trong file source, cộng scripts giảm 205-150 = 55)
Ghi con số thực tế vào commit message.

- [ ] **Step 4: Commit tổng kết**

```bash
git add docs/superpowers/specs/2026-08-01-corpus-pipeline-refactor-design.md
git commit -m "docs(corpus): note byte-identical verification results and line reduction in spec"
```

(Không có thay đổi source ở task này — nếu mọi thứ xanh, không cần commit gì thêm; nếu phát hiện lỗi thì sửa theo task liên quan.)

---

## Self-Review Notes (đã kiểm tra khi viết plan)

- **Spec coverage:** §3.1 file map → Task 1-6; §4.1 bảng flags → Task 4-5 (v6.1: allow_approved/naturalness_gate/casefold=True; v6.2: False); §4.2 CLI → Task 6; §5 gate → Task 1; §6 sequence → đúng thứ tự Task 1→7; §7 testing → Task 1/6; §8 non-goals → không đụng fresh/v6.3 dialogues+corpus, import helpers, configs; §4.3 dự phòng → áp dụng cho fresh/v6.3 (giữ nguyên hoàn toàn).
- **Điểm neo giữ nguyên:** `v6_1_dialogue_records`/`v6_2_dialogue_records` (audit script import), `build_ru_v6_1_corpus`/`build_ru_v6_2_corpus` signature, `V6_1_ROWS`/`V6_2_ROWS`, `EXPECTED_*` re-export từ dialogues (corpus wrappers import), `FRESH_AUTHORED_ROWS`/`AUTHORED_ROWS` không đụng.
- **Thứ tự dict report:** giữ nguyên văn v6.1 (naturalness_audit cuối) và v6.2 (không có) → manifest byte-identical.
- **Lỗi tiềm ẩn đã chặn:** `Random(42)` phải là Random tên biến cục bộ trong build_slots (giữ nguyên văn); `_expected_turn_ranges()` trả range mới mỗi lần; `identity_repair_en` chính là từ khác duy nhất "warm"/"natural".
