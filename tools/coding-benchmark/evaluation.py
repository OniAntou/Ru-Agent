"""Fair comparison of externally graded coding-agent trials.

This module imports predictions and verifier outcomes; it never invokes an agent.
"""
import hashlib
import json
import math
import re
from pathlib import Path

PINNED_FIELDS = ("agent", "agent_version", "model", "effort", "protocol", "toolchain", "deadline_seconds")
STATUSES = {"completed", "timeout", "interrupted", "infrastructure_error", "failed"}


def task_definition_fingerprint(path):
    """Fingerprint task instructions, baseline, grader and metadata, excluding oracle sources."""
    root = Path(path).resolve(strict=True)
    if not root.is_dir():
        raise ValueError("task definition must be a directory")
    digest = hashlib.sha256()
    files = []
    for item in root.rglob("*"):
        relative = item.relative_to(root)
        if any(part in {"solution", ".git", "bin", "obj", "__pycache__", "node_modules"}
               for part in relative.parts):
            continue
        if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
            raise ValueError("linked task definition entry")
        if item.is_file():
            files.append((relative.as_posix(), item.read_bytes()))
    if not files:
        raise ValueError("empty task definition")
    for relative, content in sorted(files):
        digest.update(relative.encode("utf-8") + b"\0" + content + b"\0")
    return digest.hexdigest()


def load_manifest(path, task_store=None):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(document, dict) or document.get("schema_version") != 1:
        raise ValueError("unsupported evaluation manifest")
    cohort = document.get("cohort")
    if not isinstance(cohort, dict) or any(not isinstance(cohort.get(key), str) or not cohort[key].strip()
                                           for key in PINNED_FIELDS if key != "deadline_seconds"):
        raise ValueError("manifest must pin agent, version, model, effort, protocol, toolchain and deadline")
    if type(cohort.get("deadline_seconds")) is not int or cohort["deadline_seconds"] <= 0:
        raise ValueError("deadline_seconds must be a positive integer")
    attempts = document.get("attempts_per_task")
    if type(attempts) is not int or not 1 <= attempts <= 20:
        raise ValueError("attempts_per_task must be between 1 and 20")
    tasks = document.get("tasks")
    if not isinstance(tasks, list) or not tasks:
        raise ValueError("manifest needs tasks")
    ids = [task.get("task_id") for task in tasks if isinstance(task, dict)]
    if len(ids) != len(tasks) or any(not isinstance(x, str) or not x for x in ids) or len(set(ids)) != len(ids):
        raise ValueError("task IDs must be nonempty and unique")
    for task in tasks:
        digest = task.get("task_sha256")
        if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError("each task needs a lowercase SHA-256 task definition fingerprint")
    if task_store is not None:
        root = Path(task_store).resolve(strict=True)
        for task_id, task in zip(ids, tasks, strict=True):
            if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]{0,160}", task_id) or ".." in Path(task_id).parts:
                raise ValueError("invalid task ID for task store")
            definition = (root / Path(task_id)).resolve(strict=True)
            if root not in definition.parents:
                raise ValueError("task definition escapes task store")
            if task_definition_fingerprint(definition) != task["task_sha256"]:
                raise ValueError("task definition fingerprint mismatch: " + task_id)
    return document


def manifest_fingerprint(document):
    canonical = json.dumps(document, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    return hashlib.sha256(canonical).hexdigest()


def _percentile(values, quantile):
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = math.floor(position)
    upper = math.ceil(position)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def compare(manifest, records):
    """Validate a complete cohort and summarize correctness separately from autonomy."""
    expected = manifest["cohort"]
    expected_tasks = {task["task_id"]: task["task_sha256"] for task in manifest["tasks"]}
    expected_keys = {(task_id, attempt) for task_id in expected_tasks
                     for attempt in range(1, manifest["attempts_per_task"] + 1)}
    seen = set()
    accepted = []
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("trial must be an object")
        if not isinstance(record.get("task_id"), str) or type(record.get("attempt")) is not int:
            raise ValueError("unexpected or duplicate trial")
        key = (record.get("task_id"), record.get("attempt"))
        if (key not in expected_keys or key in seen or type(record.get("attempt")) is not int):
            raise ValueError("unexpected or duplicate trial")
        seen.add(key)
        if record.get("task_sha256") != expected_tasks[key[0]]:
            raise ValueError("task fingerprint mismatch")
        if any(record.get(field) != expected[field] for field in PINNED_FIELDS):
            raise ValueError("incompatible cohort provenance: " + str(key))
        status = record.get("status")
        if not isinstance(status, str) or status not in STATUSES:
            raise ValueError("invalid trial status")
        if type(record.get("correct")) is not bool:
            raise ValueError("correct must be an externally graded boolean")
        if type(record.get("intervention")) is not bool:
            raise ValueError("intervention must be boolean")
        seconds = record.get("wall_seconds")
        if type(seconds) not in (float, int) or not math.isfinite(seconds) or seconds < 0:
            raise ValueError("wall_seconds must be finite and nonnegative")
        for field in ("model_attempts", "tool_attempts", "input_tokens", "output_tokens"):
            value = record.get(field)
            if value is not None and (type(value) is not int or value < 0):
                raise ValueError(field + " must be a nonnegative integer or null")
        accepted.append(record)
    missing = sorted((task, attempt) for task, attempt in expected_keys - seen)
    task_count = len(expected_tasks) * manifest["attempts_per_task"]
    durations = [r["wall_seconds"] for r in accepted]
    return {
        "manifest_sha256": manifest_fingerprint(manifest),
        "cohort": expected,
        "expected_trials": task_count,
        "received_trials": len(accepted),
        "complete": not missing,
        "missing_trials": [{"task_id": task, "attempt": attempt} for task, attempt in missing],
        "observed_correctness_rate": sum(r["correct"] for r in accepted) / len(accepted) if accepted else None,
        "correctness_rate": sum(r["correct"] for r in accepted) / task_count,
        "autonomous_completion_rate": sum(r["status"] == "completed" and r["correct"] and not r["intervention"]
                                           for r in accepted) / task_count,
        "status_counts": {status: sum(r["status"] == status for r in accepted) for status in sorted(STATUSES)},
        "interventions": sum(r["intervention"] for r in accepted),
        "latency_seconds": {"p50": _percentile(durations, .50), "p95": _percentile(durations, .95)},
        "usage": {field: (sum(r.get(field) for r in accepted if r.get(field) is not None)
                          if accepted and all(r.get(field) is not None for r in accepted) else None)
                  for field in ("model_attempts", "tool_attempts", "input_tokens", "output_tokens")},
        "usage_complete": {field: bool(accepted) and all(r.get(field) is not None for r in accepted)
                           for field in ("model_attempts", "tool_attempts", "input_tokens", "output_tokens")},
    }


def compare_cohorts(entries, track):
    """Compare named report sets with explicit matched-harness or product-track rules."""
    if track not in {"harness", "product"}:
        raise ValueError("track must be harness or product")
    if not isinstance(entries, list) or len(entries) < 2:
        raise ValueError("at least two cohorts are required")
    results = []
    baseline = None
    labels = set()
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("label"), str) or not entry["label"].strip():
            raise ValueError("each cohort needs a label")
        if entry["label"] in labels:
            raise ValueError("cohort labels must be unique")
        labels.add(entry["label"])
        manifest = entry.get("manifest")
        records = entry.get("records")
        if not isinstance(manifest, dict) or not isinstance(records, list):
            raise ValueError("each cohort needs a manifest and records")
        task_matrix = ({t["task_id"]: t["task_sha256"] for t in manifest.get("tasks", [])},
                       manifest.get("attempts_per_task"))
        pins = manifest.get("cohort", {})
        if baseline is None:
            baseline = (task_matrix, pins)
        else:
            base_matrix, base_pins = baseline
            if task_matrix != base_matrix:
                raise ValueError("cohorts must use identical task hashes and trial matrices")
            if pins.get("deadline_seconds") != base_pins.get("deadline_seconds"):
                raise ValueError("cohorts must use the same deadline")
            if pins.get("toolchain") != base_pins.get("toolchain"):
                raise ValueError("cohorts must use the same toolchain")
            if track == "harness" and any(pins.get(key) != base_pins.get(key)
                                          for key in ("model", "effort", "protocol")):
                raise ValueError("harness track requires matched model, effort and protocol")
        results.append({"label": entry["label"], "report": compare(manifest, records)})
    return {"track": track,
            "comparability": ("matched model/effort/protocol harness comparison"
                              if track == "harness" else "product configurations; model or protocol may differ"),
            "cohorts": results}


def load_records(path):
    records = []
    for line_number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise ValueError(f"invalid JSONL at line {line_number}") from error
    return records
