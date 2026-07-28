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


_MOJIBAKE_MARKERS = ("Ã", "Â", "á»", "Ä", "Æ", "â")
_URL_PATTERN = re.compile(r"(?i)(?:https?://|www\.)")
_AI_META_SAFE_PATTERN = re.compile(
    r"(?i)(?:\bas an ai\b|\blanguage model\b|\bm\u00f4 h\u00ecnh ai\b|\bng\u00f4n ng\u1eef ai\b|"
    r"\bm\u1ed9t ng\u01b0\u1eddi robot\b|\bt\u00f4i l\u00e0 robot\b|\bem l\u00e0 robot\b|\bkh\u00f4ng th\u1ec3 truy c\u1eadp\b|\bi cannot\b)"
)
_EXPLICIT_SAFE_PATTERN = re.compile(
    r"(?i)(?:\bquan h\u1ec7 t\u00ecnh d\u1ee5c\b|\bsex\b|\bsexting\b|\bc\u1ef1c kho\u00e1i\b|"
    r"\bmasturbat\w*\b|\bth\u1ee7 d\u00e2m\b|\b\u00e2m \u0111\u1ea1o\b|\bd\u01b0\u01a1ng v\u1eadt\b|\b\u0111\u1ee5\b|"
    r"\b(orgasm|sexual intercourse|genitals|penis|vagina|rape|cum|fuck)\b|"
    r"\bc\u01b0\u1ee1ng hi\u1ebfp\b|\bhi\u1ebfp d\u00e2m\b|\bc\u01b0\u1ee1ng \u00e9p t\u00ecnh d\u1ee5c\b)"
)
_MINOR_SAFE_PATTERN = re.compile(r"(?i)(?:\btr\u1ebb em\b|\bcon n\u00edt\b|\bv\u1ecb th\u00e0nh ni\u00ean\b|\bminor\b)")
_HARM_SAFE_PATTERN = re.compile(
    r"(?i)(?:\bgi\u1ebft anh\b|\bgi\u1ebft em\b|\bkill you\b|\bshoot you\b|\bch\u1eb7t \u0111\u1ea7u\b|\bm\u00e1u me\b|"
    r"\bb\u1eafn anh\b|\bb\u1eafn em\b|\bblood everywhere\b|\bgraphic violence\b|\b\u0111\u00e1nh ch\u1ebft\b)"
)
_VTS_PATTERN = re.compile(r"\[\[\s*vts\s*:", re.IGNORECASE)
_AI_META_PATTERN = re.compile(
    r"(?i)(?:\bas an ai\b|\blanguage model\b|\bmô hình ai\b|\bngôn ngữ ai\b|"
    r"\bmột người robot\b|\btôi là robot\b|\bem là robot\b|\bkhông thể truy cập\b|"
    r"\bi cannot\b|\bi do not have a physical body\b)"
)
_EXPLICIT_PATTERN = re.compile(
    r"(?i)(?:\bquan hệ tình dục\b|\bsex\b|\bsexting\b|\bcực khoái\b|"
    r"\bmasturbat\w*\b|\bthủ dâm\b|\bâm đạo\b|\bdương vật\b|\bđụ\b|"
    r"\bfuck\b|\bcưỡng hiếp\b|\bhiếp dâm\b|\bcưỡng ép tình dục\b|"
    r"\bcoercive sex\b|\bexplicit sexual\b|\btinh túy\b)"
)
_MINOR_PATTERN = re.compile(r"(?i)(?:\btrẻ em\b|\bcon nít\b|\bvị thành niên\b|\bminor\b)")
_HARM_PATTERN = re.compile(
    r"(?i)(?:\bgiết anh\b|\bgiết em\b|\bkill you\b|\bchặt đầu\b|\bmáu me\b|"
    r"\bbắn anh\b|\bbắn em\b|\bgraphic violence\b|\bđánh chết\b)"
)
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


def _reject_reason(turn: SourceTurn) -> str | None:
    prompt = normalize_text(turn.prompt)
    response = normalize_text(turn.response)
    if not prompt or not response:
        return "malformed"
    combined = f"{prompt}\n{response}"
    if _VTS_PATTERN.search(combined):
        return "vts_control_marker"
    if _AI_META_PATTERN.search(combined) or _AI_META_SAFE_PATTERN.search(combined):
        return "ai_meta"
    if _URL_PATTERN.search(combined) or len(_LIST_LINE_PATTERN.findall(response)) >= 2:
        return "links_or_list"
    if _EXPLICIT_PATTERN.search(combined) or _EXPLICIT_SAFE_PATTERN.search(combined):
        return "unsafe_explicit"
    if _MINOR_PATTERN.search(combined) or _MINOR_SAFE_PATTERN.search(combined):
        return "unsafe_minor"
    if _HARM_PATTERN.search(combined) or _HARM_SAFE_PATTERN.search(combined):
        return "unsafe_harm"
    if len(prompt) > 800 or len(response) > 1600:
        return "too_long"
    return None


def _to_training_row(turn: SourceTurn) -> dict[str, object]:
    language = _language(turn.prompt, turn.response)
    response = turn.response.removesuffix("Nya~").rstrip() + " Nya~"
    return {
        "messages": [
            {"role": "system", "content": ru_system_prompt_for(language)},
            {"role": "user", "content": turn.prompt},
        ],
        "response": response,
        "language": language,
        "mode": "memory_calibration" if turn.source == "memory-import" else "approved_calibration",
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
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


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
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report
