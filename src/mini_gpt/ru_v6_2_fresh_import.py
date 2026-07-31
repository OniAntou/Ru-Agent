from __future__ import annotations

import json
import re
from dataclasses import dataclass
from hashlib import sha256
from pathlib import Path
from typing import Any

from ._base_corpus import write_jsonl
from ._text_utils import normalize_text
from .ru_style import ru_system_prompt_for


_URL_PATTERN = re.compile(r"(?i)(?:https?://|www\.)")
_AI_META_PATTERN = re.compile(
    r"(?i)(?:\bas an ai\b|\blanguage model\b|\bmô hình ai\b|\bngôn ngữ ai\b|"
    r"\bmột người robot\b|\btôi là robot\b|\bem là robot\b|\bkhông thể truy cập\b|"
    r"\bi cannot\b|\bi do not have a physical body\b)"
)
_EXPLICIT_PATTERN = re.compile(
    r"(?i)(?:\bquan hệ tình dục\b|\bsex\b|\bsexting\b|\bcực khoái\b|"
    r"\bmasturbat\w*\b|\bthủ dâm\b|\bâm đạo\b|\bdương vật\b|\bđụ\b|"
    r"\b(orgasm|sexual intercourse|genitals|penis|vagina|rape|cum|fuck)\b|"
    r"\bcưỡng hiếp\b|\bhiếp dâm\b|\bcưỡng ép tình dục\b|"
    r"\bcoercive sex\b|\bexplicit sexual\b|\btinh túy\b)"
)
_MINOR_PATTERN = re.compile(r"(?i)(?:\btrẻ em\b|\bcon nít\b|\bvị thành niên\b|\bminor\b)")
_HARM_PATTERN = re.compile(
    r"(?i)(?:\bgiết anh\b|\bgiết em\b|\bkill you\b|\bshoot you\b|\bchặt đầu\b|\bmáu me\b|"
    r"\bbắn anh\b|\bbắn em\b|\bblood everywhere\b|\bgraphic violence\b|\bđánh chết\b)"
)
_VTS_PATTERN = re.compile(r"\[\[\s*vts\s*:", re.IGNORECASE)
_LIST_LINE_PATTERN = re.compile(r"(?m)^\s*(?:[-*•]|\d+[.)])\s+")
_PAIR_PATTERN = re.compile(r"(?ms)User\s*:\s*(.*?)\s*Assistant\s*:\s*(.*?)(?=\n\s*User\s*:|\Z)")
_VI_PATTERN = re.compile(r"(?i)(?:\b(?:anh|em|mình|tôi|không|đang|vừa|hỏi|thăm|chào|nói|về|đây|nhé|rồi|muốn)\b|[à-ỹ])")
_EN_PATTERN = re.compile(r"(?i)\b(?:the|and|you|your|i|am|is|are|want|need|hello|please|here|with|this|that)\b")


@dataclass(frozen=True)
class SourceTurn:
    prompt: str
    response: str
    source: str
    source_id: str


@dataclass(frozen=True)
class ImportSelection:
    rows: list[dict[str, object]]
    rejected: list[dict[str, str]]
    source_hashes: dict[str, str]


def repair_mojibake(text: str) -> str:
    """Repair the project's recurring UTF-8-as-Latin-1 display corruption."""
    try:
        repaired = text.encode("latin1").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return text
    markers = ("\u00c3", "\u00c2", "\u00e1\u00bb", "\u00c4", "\u00c6", "\u00e2")
    before = sum(text.count(marker) for marker in markers)
    after = sum(repaired.count(marker) for marker in markers)
    return repaired if after < before else text


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    records: list[dict[str, Any]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            records.append({"__malformed__": str(line_number)})
            continue
        records.append(value if isinstance(value, dict) else {"__malformed__": str(line_number)})
    return records


def load_approved_rows(path: str | Path) -> list[SourceTurn]:
    rows: list[SourceTurn] = []
    for index, raw in enumerate(_read_jsonl(Path(path))):
        identifier = raw.get("id")
        prompt = raw.get("prompt")
        response = raw.get("response")
        if not all(isinstance(value, str) for value in (identifier, prompt, response)):
            rows.append(SourceTurn("", "", "approved-import", f"approved:{index}"))
            continue
        rows.append(
            SourceTurn(
                normalize_text(repair_mojibake(prompt)),
                normalize_text(repair_mojibake(response)),
                "approved-import",
                identifier,
            )
        )
    return rows


def load_memory_rows(path: str | Path) -> list[SourceTurn]:
    source_path = Path(path)
    if not source_path.exists():
        return []
    try:
        values = json.loads(source_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return [SourceTurn("", "", "memory-import", "memory:0")]
    if not isinstance(values, list):
        return [SourceTurn("", "", "memory-import", "memory:0")]
    rows: list[SourceTurn] = []
    for index, value in enumerate(values):
        if not isinstance(value, str):
            rows.append(SourceTurn("", "", "memory-import", f"memory:{index}"))
            continue
        repaired = repair_mojibake(value)
        matches = list(_PAIR_PATTERN.finditer(repaired))
        if not matches:
            rows.append(SourceTurn("", "", "memory-import", f"memory:{index}"))
            continue
        for pair_index, match in enumerate(matches):
            rows.append(
                SourceTurn(
                    normalize_text(match.group(1)),
                    normalize_text(match.group(2)),
                    "memory-import",
                    f"memory:{index}:{pair_index}",
                )
            )
    return rows


def _language(prompt: str, response: str) -> str:
    text = f"{prompt} {response}"
    has_vi = bool(_VI_PATTERN.search(text))
    has_en = bool(_EN_PATTERN.search(text))
    if has_vi and has_en:
        return "mixed"
    if has_en:
        return "en"
    return "vi"


_MAX_PROMPT_CHARS = 800
_MAX_RESPONSE_CHARS = 1600
CALIBRATION_MODES = frozenset({"approved_calibration", "memory_calibration"})


def _reject_reason(turn: SourceTurn) -> str | None:
    prompt = normalize_text(turn.prompt)
    response = normalize_text(turn.response)
    if not prompt or not response:
        return "malformed"
    combined = f"{prompt}\n{response}"
    if _VTS_PATTERN.search(combined):
        return "vts_control_marker"
    if _AI_META_PATTERN.search(combined):
        return "ai_meta"
    if _URL_PATTERN.search(combined) or len(_LIST_LINE_PATTERN.findall(response)) >= 2:
        return "links_or_list"
    if _EXPLICIT_PATTERN.search(combined):
        return "unsafe_explicit"
    if _MINOR_PATTERN.search(combined):
        return "unsafe_minor"
    if _HARM_PATTERN.search(combined):
        return "unsafe_harm"
    if len(prompt) > _MAX_PROMPT_CHARS or len(response) > _MAX_RESPONSE_CHARS:
        return "too_long"
    return None


def _to_training_row(turn: SourceTurn) -> dict[str, object]:
    language = _language(turn.prompt, turn.response)
    response = turn.response.removesuffix("Nya~").rstrip()
    mode = "memory_calibration" if turn.source == "memory-import" else "approved_calibration"
    if mode not in CALIBRATION_MODES:
        raise ValueError(f"unexpected calibration mode: {mode}")
    return {
        "messages": [
            {"role": "system", "content": ru_system_prompt_for(language)},
            {"role": "user", "content": turn.prompt},
        ],
        "response": response,
        "language": language,
        "mode": mode,
        "intensity": "light",
        "turn_band": "short",
        "source": turn.source,
        "license": "project-owned",
        "import_id": turn.source_id,
    }


def _prompt_key(row: dict[str, object]) -> str:
    messages = row["messages"]
    assert isinstance(messages, list)
    return str(messages[-1]["content"]).casefold()


def _pair_key(row: dict[str, object]) -> tuple[str, str]:
    return (_prompt_key(row), str(row["response"]).casefold())


def _source_sha256(path: Path) -> str | None:
    return sha256(path.read_bytes()).hexdigest() if path.exists() else None


def select_import_rows(approved_path: str | Path, memory_path: str | Path) -> ImportSelection:
    approved = Path(approved_path)
    memory = Path(memory_path)
    candidates = [*load_approved_rows(approved), *load_memory_rows(memory)]
    accepted: list[dict[str, object]] = []
    rejected: list[dict[str, str]] = []
    pairs: set[tuple[str, str]] = set()
    prompts: set[str] = set()
    for turn in candidates:
        reason = _reject_reason(turn)
        if reason is not None:
            rejected.append({"source": turn.source, "id": turn.source_id, "reason": reason})
            continue
        row = _to_training_row(turn)
        pair = _pair_key(row)
        prompt = _prompt_key(row)
        if pair in pairs:
            rejected.append({"source": turn.source, "id": turn.source_id, "reason": "duplicate_pair"})
            continue
        if prompt in prompts:
            rejected.append({"source": turn.source, "id": turn.source_id, "reason": "duplicate_prompt"})
            continue
        pairs.add(pair)
        prompts.add(prompt)
        accepted.append(row)
    return ImportSelection(
        rows=accepted,
        rejected=rejected,
        source_hashes={
            "approved.jsonl": _source_sha256(approved),
            "memory_texts.json": _source_sha256(memory),
        },
    )


def _read_rows(path: Path) -> list[dict[str, object]]:
    if not path.exists():
        return []
    records: list[dict[str, object]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError as error:
            raise ValueError(f"malformed jsonl row in {path} at line {line_number}") from error
        if not isinstance(value, dict):
            raise ValueError(f"malformed jsonl row in {path} at line {line_number}")
        records.append(value)
    return records


def append_imports_to_train(dataset_dir: str | Path, selection: ImportSelection) -> dict[str, object]:
    destination = Path(dataset_dir)
    train_path = destination / "train.jsonl"
    manifest_path = destination / "manifest.json"
    train = _read_rows(train_path)
    validation_before = (destination / "val.jsonl").read_bytes()
    held_out_before = (destination / "held_out.jsonl").read_bytes()
    base_train = [row for row in train if row.get("source") not in {"approved-import", "memory-import"}]
    existing_prompts = {_prompt_key(row) for row in [*base_train, *_read_rows(destination / "val.jsonl"), *_read_rows(destination / "held_out.jsonl")]}
    merged = list(base_train)
    skipped_existing = 0
    for row in selection.rows:
        if _prompt_key(row) in existing_prompts:
            skipped_existing += 1
            continue
        merged.append(row)
        existing_prompts.add(_prompt_key(row))
    partial = train_path.with_suffix(train_path.suffix + ".partial")
    write_jsonl(partial, merged)
    partial.replace(train_path)
    if (destination / "val.jsonl").read_bytes() != validation_before or (destination / "held_out.jsonl").read_bytes() != held_out_before:
        raise RuntimeError("import merge modified validation or held-out data")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    split_counts = {"train": len(merged), "validation": len(_read_rows(destination / "val.jsonl")), "held_out": len(_read_rows(destination / "held_out.jsonl"))}
    imported = merged[len(base_train):]
    for field in ("language", "mode", "intensity", "turn_band", "source"):
        key = f"{field}_counts"
        if key not in manifest:
            continue
        counts = dict(manifest[key])
        for row in imported:
            value = str(row[field])
            counts[value] = counts.get(value, 0) + 1
        manifest[key] = counts
    report: dict[str, object] = {
        "source_hashes": selection.source_hashes,
        "accepted_count": len(selection.rows) - skipped_existing,
        "selected_count": len(selection.rows),
        "skipped_existing": skipped_existing,
        "rejected_count": len(selection.rejected),
        "rejected": selection.rejected,
        "final_train_sha256": sha256(train_path.read_bytes()).hexdigest(),
    }
    manifest["split_counts"] = split_counts
    manifest["total_examples"] = sum(split_counts.values())
    manifest["imported_calibration"] = report
    manifest_partial = manifest_path.with_suffix(manifest_path.suffix + ".partial")
    manifest_partial.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    manifest_partial.replace(manifest_path)
    return report
