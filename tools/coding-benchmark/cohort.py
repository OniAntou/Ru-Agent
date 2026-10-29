"""Cohort manifests and attempt aggregation for controlled agent evaluations.

This module only consumes trusted grader/observation records. It never runs an
agent, grader or provider, so GUI and CLI runners can share the record contract.
"""
import hashlib
import json
import math
import re
from pathlib import Path

from evaluation import manifest_fingerprint

COMMIT = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
COHORT_PINS = (
    "agent", "agent_version", "source_commit", "source_tree_sha256", "artifact_sha256",
    "model", "effort", "protocol",
    "tool_profile", "toolchain", "deadline_seconds",
)
STATUSES = {
    "completed", "parked", "interrupted", "infrastructure_error", "timeout", "failed",
}
TRAJECTORY_METRICS = (
    "model_steps", "tool_dispatches", "tool_receipts", "tool_errors", "repair_attempts",
    "repeated_reads", "recovery_attempts", "compactions",
)
USAGE_METRICS = (
    "input_tokens", "output_tokens", "cached_input_tokens", "search_calls", "cost",
)


def _field(task, name):
    return task.get(name) if isinstance(task, dict) else getattr(task, name, None)


def _hash_text(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _task_id(task):
    value = _field(task, "task_id") or _field(task, "id")
    if not isinstance(value, str) or not value.strip():
        raise ValueError("each cohort task needs a task ID")
    return value


def _task_hash(task):
    value = _field(task, "task_sha256")
    if value is None:
        path = _field(task, "path")
        if path is not None:
            from evaluation import task_definition_fingerprint
            value = task_definition_fingerprint(path)
        else:
            value = _field(task, "fixture_hash")
    return value


def create_manifest(tasks, cohort, *, attempts_per_task=1):
    """Freeze identity pins and prompt/fixture hashes without exposing task text."""
    if not isinstance(cohort, dict):
        raise ValueError("cohort pins must be an object")
    missing = [key for key in COHORT_PINS if key not in cohort]
    if missing:
        raise ValueError("missing cohort pin: " + ", ".join(missing))
    if any(not isinstance(cohort[key], str) or not cohort[key].strip()
           for key in COHORT_PINS if key != "deadline_seconds"):
        raise ValueError("cohort identity pins must be nonempty strings")
    if "provider_identity" in cohort and (not isinstance(cohort["provider_identity"], str)
                                           or not cohort["provider_identity"].strip()):
        raise ValueError("provider_identity must be a nonempty string when supplied")
    if not isinstance(cohort["source_commit"], str) or COMMIT.fullmatch(cohort["source_commit"]) is None:
        raise ValueError("source_commit must be a full lowercase Git commit hash")
    if not _is_sha256(cohort["artifact_sha256"]):
        raise ValueError("artifact_sha256 must be a lowercase SHA-256")
    if not _is_sha256(cohort["source_tree_sha256"]):
        raise ValueError("source_tree_sha256 must be a lowercase SHA-256")
    if type(cohort["deadline_seconds"]) is not int or cohort["deadline_seconds"] <= 0:
        raise ValueError("deadline_seconds must be a positive integer")
    if type(attempts_per_task) is not int or not 1 <= attempts_per_task <= 20:
        raise ValueError("attempts_per_task must be between 1 and 20")
    if not isinstance(tasks, (list, tuple)) or not tasks:
        raise ValueError("cohort requires at least one task")
    entries = []
    seen = set()
    for task in tasks:
        task_id = _task_id(task)
        fixture_sha256 = _field(task, "fixture_sha256") or _field(task, "fixture_hash")
        prompt = _field(task, "prompt")
        if prompt is None:
            prompt = _field(task, "instruction")
        if not isinstance(prompt, str) or not prompt:
            raise ValueError("each cohort task needs its effective prompt")
        task_sha256 = _task_hash(task)
        if not _is_sha256(task_sha256) or not _is_sha256(fixture_sha256):
            raise ValueError("each task needs lowercase SHA-256 task and fixture identities")
        if task_id in seen:
            raise ValueError("task IDs must be unique")
        seen.add(task_id)
        entries.append({
            "task_id": task_id,
            "task_sha256": task_sha256,
            "fixture_sha256": fixture_sha256,
            "prompt_sha256": _hash_text(prompt),
        })
        if _field(task, "skill_scenario") is not None:
            expected_selection = _field(task, "expected_skill_selection")
            if not isinstance(expected_selection, str) or expected_selection not in {"required", "none"}:
                raise ValueError("skill task needs an expected selection label")
            entries[-1]["skill_scenario"] = _field(task, "skill_scenario")
            entries[-1]["expected_skill_selection"] = expected_selection
    return {"schema_version": 2, "cohort": dict(cohort),
            "attempts_per_task": attempts_per_task, "tasks": entries}


def _is_sha256(value):
    return (isinstance(value, str) and len(value) == 64
            and all(char in "0123456789abcdef" for char in value))


def record_attempt(
    manifest, task_id, attempt, *, status, grader_accepted, unattended_completed,
    intervention, wall_seconds, base_commit=None, trajectory=None, usage=None,
    grader=None, source=None, skill_selection=None,
):
    """Build a validated import record from a CLI, GUI or external observation."""
    if manifest.get("schema_version") != 2:
        raise ValueError("attempt records require a schema-v2 manifest")
    task = next((item for item in manifest["tasks"] if item["task_id"] == task_id), None)
    if task is None:
        raise ValueError("unknown task ID")
    if type(attempt) is not int or not 1 <= attempt <= manifest["attempts_per_task"]:
        raise ValueError("attempt must be inside the manifest trial matrix")
    if status not in STATUSES:
        raise ValueError("invalid attempt status")
    if grader_accepted is not None and type(grader_accepted) is not bool:
        raise ValueError("grader_accepted must be an externally graded boolean or unknown")
    if type(unattended_completed) is not bool or type(intervention) is not bool:
        raise ValueError("unattended_completed and intervention must be booleans")
    if unattended_completed and (status != "completed" or intervention):
        raise ValueError("unattended completion requires a completed, non-intervened run")
    if status == "parked" and not intervention:
        raise ValueError("parked status requires an intervention")
    if skill_selection is not None and not isinstance(skill_selection, str):
        raise ValueError("skill_selection must be a string or unknown")
    if type(wall_seconds) not in (int, float) or not math.isfinite(wall_seconds) or wall_seconds < 0:
        raise ValueError("wall_seconds must be finite and nonnegative")
    if base_commit is not None and (not isinstance(base_commit, str) or COMMIT.fullmatch(base_commit) is None):
        raise ValueError("base_commit must be a full lowercase Git commit hash")
    record = {
        **manifest["cohort"],
        "task_id": task_id,
        "task_sha256": task["task_sha256"],
        "fixture_sha256": task["fixture_sha256"],
        "prompt_sha256": task["prompt_sha256"],
        "attempt": attempt,
        "base_commit": base_commit,
        "status": status,
        "grader_accepted": grader_accepted,
        "unattended_completed": unattended_completed,
        "intervention": intervention,
        "wall_seconds": wall_seconds,
        "trajectory": _metrics(trajectory, TRAJECTORY_METRICS),
        "usage": _metrics(usage, USAGE_METRICS),
        "grader": grader,
        "source": source,
    }
    if task.get("skill_scenario") is not None:
        record["skill_eval"] = {
            "scenario": task["skill_scenario"],
            "expected_skill_selection": task.get("expected_skill_selection"),
            "observed_skill_selection": skill_selection if isinstance(skill_selection, str) else "unknown",
        }
    return record


def record_ru_attempt(manifest, task_id, attempt, execution, grade, *, base_commit):
    """Adapt existing native RPC and external grader outputs into the shared record."""
    if not isinstance(execution, dict) or not isinstance(grade, dict):
        raise ValueError("execution and trusted grader results must be objects")
    for pin, field in (("model", "provider_model"), ("protocol", "provider_protocol"),
                       ("tool_profile", "tool_profile")):
        observed = execution.get(field)
        expected = manifest.get("cohort", {}).get(pin)
        if observed != expected:
            raise ValueError("observed " + pin + " does not match the pinned cohort")
    raw_status = execution.get("status")
    status = {
        "completed": "completed", "intervention_required": "parked", "interrupted": "interrupted",
        "timed_out": "timeout", "unavailable": "infrastructure_error",
    }.get(raw_status, "failed")
    if raw_status == "failed" and execution.get("run_id") is None:
        status = "infrastructure_error"
    metrics = execution.get("tool_metrics")
    trajectory = None
    if isinstance(metrics, dict):
        metrics_complete = metrics.get("complete") is True
        trajectory = {
            "model_steps": metrics.get("observed_model_steps") if metrics_complete else None,
            "tool_dispatches": metrics.get("observed_tool_dispatches") if metrics_complete else None,
            "tool_receipts": metrics.get("observed_tool_receipts") if metrics_complete else None,
            "tool_errors": metrics.get("tool_errors"),
            "repair_attempts": metrics.get("repair_attempts"),
            "repeated_reads": metrics.get("repeated_reads"),
            "recovery_attempts": metrics.get("recovery_attempts"),
            "compactions": metrics.get("observed_compactions"),
        }
    raw_usage = execution.get("token_usage")
    usage = None
    if isinstance(raw_usage, dict):
        complete = execution.get("usage_complete") is True
        usage = {
            "input_tokens": raw_usage.get("input_tokens") if complete else None,
            "output_tokens": raw_usage.get("output_tokens") if complete else None,
            "cached_input_tokens": None,
            "search_calls": None,
            "cost": None,
        }
    return record_attempt(
        manifest, task_id, attempt, status=status,
        grader_accepted=(True if grade.get("status") == "passed" and grade.get("reward") == 1 else
                         False if grade.get("status") in {"failed", "invalid"} and grade.get("reward") == 0 else None),
        unattended_completed=raw_status == "completed",
        intervention=raw_status == "intervention_required",
        wall_seconds=execution.get("elapsed_seconds"), base_commit=base_commit,
        trajectory=trajectory, usage=usage,
        grader={"status": grade.get("status"), "groups": grade.get("groups")},
        source={"runner": "ru-native-rpc", "run_status": execution.get("run_status"),
                "terminal_error_code": execution.get("terminal_error_code")},
    )


def _metrics(value, fields):
    if value is None:
        return dict.fromkeys(fields)
    if not isinstance(value, dict):
        raise ValueError("metrics must be an object or unknown")
    result = {}
    for field in fields:
        item = value.get(field)
        if item is not None:
            if field == "cost":
                if type(item) not in (int, float) or not math.isfinite(item) or item < 0:
                    raise ValueError(field + " must be a finite nonnegative number or unknown")
            elif type(item) is not int or item < 0:
                raise ValueError(field + " must be a nonnegative integer or unknown")
        result[field] = item
    return result


def _validate_v2(manifest, records):
    if not isinstance(manifest, dict) or manifest.get("schema_version") != 2:
        raise ValueError("unsupported cohort manifest")
    cohort = manifest.get("cohort")
    if not isinstance(cohort, dict) or any(key not in cohort for key in COHORT_PINS):
        raise ValueError("manifest must pin source commit, model, protocol, tool profile and runtime")
    if COMMIT.fullmatch(cohort.get("source_commit", "")) is None:
        raise ValueError("invalid pinned source_commit")
    if not _is_sha256(cohort.get("artifact_sha256")):
        raise ValueError("invalid pinned artifact_sha256")
    if not _is_sha256(cohort.get("source_tree_sha256")):
        raise ValueError("invalid pinned source_tree_sha256")
    tasks = manifest.get("tasks")
    if not isinstance(tasks, list) or not tasks:
        raise ValueError("manifest needs tasks")
    task_map = {}
    for item in tasks:
        if not isinstance(item, dict):
            raise ValueError("invalid task manifest entry")
        task_id = item.get("task_id")
        if not isinstance(task_id, str) or not task_id or task_id in task_map:
            raise ValueError("task IDs must be nonempty and unique")
        if any(not _is_sha256(item.get(field)) for field in
               ("task_sha256", "fixture_sha256", "prompt_sha256")):
            raise ValueError("task entry must pin task, fixture and prompt SHA-256")
        if item.get("skill_scenario") is not None and item.get("expected_skill_selection") not in {"required", "none"}:
            raise ValueError("skill task manifest needs an expected selection label")
        if item.get("skill_scenario") is None and "expected_skill_selection" in item:
            raise ValueError("skill expectation requires a skill scenario")
        if item.get("skill_scenario") is not None:
            scenario = item["skill_scenario"]
            selection = item["expected_skill_selection"]
            if scenario not in {"explicit", "implicit", "negative_unrelated", "negative_manual_only", "supporting_resource"}:
                raise ValueError("invalid skill scenario")
            requires_skill = scenario in {"explicit", "implicit", "supporting_resource"}
            if (selection == "required") != requires_skill:
                raise ValueError("skill scenario and expected selection disagree")
        task_map[task_id] = item
    attempts = manifest.get("attempts_per_task")
    if type(attempts) is not int or not 1 <= attempts <= 20:
        raise ValueError("attempts_per_task must be between 1 and 20")
    expected = {(task_id, attempt) for task_id in task_map for attempt in range(1, attempts + 1)}
    seen = set()
    base_commits = {}
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("attempt record must be an object")
        key = (record.get("task_id"), record.get("attempt"))
        if key not in expected or key in seen:
            raise ValueError("unexpected or duplicate attempt")
        seen.add(key)
        task = task_map[key[0]]
        if any(record.get(field) != cohort[field] for field in COHORT_PINS):
            raise ValueError("incompatible pinned cohort identity")
        if "provider_identity" in cohort and record.get("provider_identity") != cohort["provider_identity"]:
            raise ValueError("provider identity does not match pinned cohort")
        if "provider_identity" not in cohort and "provider_identity" in record:
            raise ValueError("provider identity must be pinned by the manifest")
        if any(record.get(field) != task[field] for field in
               ("task_sha256", "fixture_sha256", "prompt_sha256")):
            raise ValueError("task, fixture or prompt fingerprint mismatch")
        if task.get("skill_scenario") is not None:
            expected_skill = {
                "scenario": task["skill_scenario"],
                "expected_skill_selection": task.get("expected_skill_selection"),
                "observed_skill_selection": record.get("skill_eval", {}).get("observed_skill_selection", "unknown")
                if isinstance(record.get("skill_eval"), dict) else None,
            }
            if record.get("skill_eval") != expected_skill:
                raise ValueError("skill evaluation provenance mismatch")
        elif "skill_eval" in record:
            raise ValueError("skill observation is not valid for a non-skill task")
        if record.get("status") not in STATUSES:
            raise ValueError("invalid attempt status")
        if record.get("grader_accepted") is not None and type(record.get("grader_accepted")) is not bool:
            raise ValueError("grader_accepted must be an externally graded boolean or unknown")
        if type(record.get("unattended_completed")) is not bool:
            raise ValueError("unattended_completed must be boolean")
        if type(record.get("intervention")) is not bool:
            raise ValueError("intervention must be boolean")
        if record["unattended_completed"] and (record["status"] != "completed" or record["intervention"]):
            raise ValueError("unattended completion requires a completed, non-intervened run")
        if record["status"] == "parked" and not record["intervention"]:
            raise ValueError("parked status requires an intervention")
        seconds = record.get("wall_seconds")
        if type(seconds) not in (int, float) or not math.isfinite(seconds) or seconds < 0:
            raise ValueError("wall_seconds must be finite and nonnegative")
        base_commit = record.get("base_commit")
        if base_commit is not None and (not isinstance(base_commit, str) or COMMIT.fullmatch(base_commit) is None):
            raise ValueError("invalid base_commit")
        if base_commit is not None:
            previous = base_commits.setdefault(key[0], base_commit)
            if previous != base_commit:
                raise ValueError("base_commit mismatch across task attempts")
        for group, fields in (("trajectory", TRAJECTORY_METRICS), ("usage", USAGE_METRICS)):
            values = record.get(group)
            if not isinstance(values, dict):
                raise ValueError(group + " must be an object")
            _metrics(values, fields)
    return task_map, expected, seen


def aggregate(manifest, records):
    """Aggregate outcomes without treating missing telemetry as zero or success."""
    records = list(records)
    task_map, expected, seen = _validate_v2(manifest, records)
    known_graded = [record for record in records if record["grader_accepted"] is not None]
    accepted = sum(record["grader_accepted"] is True for record in known_graded)
    grader_complete = len(known_graded) == len(expected)
    completed = sum(record["unattended_completed"] for record in records)
    durations = [record["wall_seconds"] for record in records]
    status_counts = {status: sum(record["status"] == status for record in records)
                     for status in sorted(STATUSES)}
    per_task = []
    for task_id in task_map:
        task_records = [record for record in records if record["task_id"] == task_id]
        known = [record for record in task_records if record["grader_accepted"] is not None]
        accepted_count = sum(record["grader_accepted"] is True for record in known)
        per_task.append({
            "task_id": task_id,
            "expected_attempts": manifest["attempts_per_task"],
            "received_attempts": len(task_records),
            "grader_accepted_attempts": accepted_count,
            "grader_evidence_complete": len(known) == manifest["attempts_per_task"],
            "observed_grader_acceptance_rate": accepted_count / len(known) if known else None,
            "unattended_completions": sum(record["unattended_completed"] for record in task_records),
            "status_counts": {status: sum(record["status"] == status for record in task_records)
                              for status in sorted(STATUSES)},
            **({"skill_eval": {"scenario": task_map[task_id]["skill_scenario"],
                                "expected_skill_selection": task_map[task_id].get("expected_skill_selection"),
                                "observed_skill_selection": _observed_skill_selection(task_records)}
               } if task_map[task_id].get("skill_scenario") is not None else {}),
        })
    report = {
        "cohort": manifest["cohort"],
        "expected_trials": len(expected),
        "schema_version": 2,
        "manifest_sha256": manifest_fingerprint(manifest),
        "received_trials": len(records),
        "complete": seen == expected,
        "missing_trials": [{"task_id": task, "attempt": attempt}
                           for task, attempt in sorted(expected - seen)],
        "grader_evidence_complete": grader_complete,
        "grader_acceptance_rate": accepted / len(expected) if grader_complete else None,
        "observed_grader_acceptance_rate": accepted / len(known_graded) if known_graded else None,
        "grader_acceptance_lower_bound": accepted / len(expected),
        "unattended_completion_rate": completed / len(expected),
        "observed_unattended_completion_rate": completed / len(records) if records else None,
        "unattended_accepted_rate": (sum(r["grader_accepted"] is True and r["unattended_completed"]
                                          and not r["intervention"] for r in records) / len(expected)
                                     if grader_complete else None),
        "status_counts": status_counts,
        "latency_seconds": {"p50": _percentile(durations, .50), "p95": _percentile(durations, .95)},
        "trajectory": _aggregate_metric_totals(records, "trajectory", TRAJECTORY_METRICS, len(expected)),
        "trajectory_complete": _aggregate_metric_completeness(records, "trajectory", TRAJECTORY_METRICS,
                                                                 len(expected)),
        "usage": _aggregate_metric_totals(records, "usage", USAGE_METRICS, len(expected)),
        "usage_complete": _aggregate_metric_completeness(records, "usage", USAGE_METRICS, len(expected)),
        "per_task": per_task,
        "attempts": records,
    }
    return report


def compare_cohorts(entries, *, track="harness"):
    """Compare named baseline/candidate reports with identical task identities."""
    track_aliases = {
        "harness": "matched-model", "matched-model": "matched-model",
        "product": "native-product", "native-product": "native-product",
    }
    if track not in track_aliases:
        raise ValueError("track must be matched-model or native-product")
    normalized_track = track_aliases[track]
    if not isinstance(entries, list) or len(entries) < 2:
        raise ValueError("at least two cohorts are required")
    normalized, configurations, labels, reference = [], [], set(), None
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("label"), str) or not entry["label"].strip():
            raise ValueError("each cohort needs a label")
        if entry["label"] in labels:
            raise ValueError("cohort labels must be unique")
        labels.add(entry["label"])
        manifest, records = entry.get("manifest"), entry.get("records")
        if not isinstance(manifest, dict) or not isinstance(records, list):
            raise ValueError("each cohort needs a manifest and records")
        _validate_v2(manifest, records)
        task_matrix = ({task["task_id"]: (task["task_sha256"], task["fixture_sha256"], task["prompt_sha256"])
                        for task in manifest["tasks"]}, manifest["attempts_per_task"])
        pins = manifest["cohort"]
        if reference is None:
            reference = task_matrix, pins
        else:
            expected_matrix, expected_pins = reference
            if task_matrix != expected_matrix:
                raise ValueError("cohorts must use identical task, fixture and prompt hashes and trial matrices")
            if any(pins.get(key) != expected_pins.get(key) for key in ("toolchain", "deadline_seconds")):
                raise ValueError("cohorts must use the same deadline and toolchain")
            if normalized_track == "matched-model" and any(
                    pins.get(key) != expected_pins.get(key)
                    for key in ("model", "effort", "protocol", "tool_profile")):
                raise ValueError("matched-model track requires matched model, effort, protocol and tool profile")
            provider_values = (pins.get("provider_identity"), expected_pins.get("provider_identity"))
            if normalized_track == "matched-model" and any(value is not None for value in provider_values):
                if not all(isinstance(value, str) and value for value in provider_values) or provider_values[0] != provider_values[1]:
                    raise ValueError("matched-model track requires matching provider identity when supplied")
        report = aggregate(manifest, records)
        normalized.append({"label": entry["label"], "report": report})
        configurations.append(dict(manifest["cohort"]))
    incomplete = any(not item["report"]["complete"] for item in normalized)
    comparable = not incomplete
    paired = None if not comparable else _paired_outcomes(normalized)
    differences = []
    if normalized_track == "native-product":
        for field in ("agent", "agent_version", "provider_identity", "model", "effort", "protocol", "tool_profile"):
            values = {item.get(field) for item in configurations}
            if len(values) > 1:
                differences.append({
                    "field": field,
                    "values": [{"cohort": item["label"], "value": configuration.get(field)}
                               for item, configuration in zip(normalized, configurations, strict=True)],
                })
    return {
        "track": track,
        "comparable": comparable,
        "incomparability_reasons": ["incomplete attempt matrix"] if incomplete else [],
        "comparability": ("matched model/provider/effort/protocol/tool-profile comparison"
                          if normalized_track == "matched-model" else
                          "native product comparison; configuration differences are reported"),
        "configuration_differences": differences,
        "paired_outcomes": paired,
        "cohorts": normalized,
    }


def _paired_outcomes(cohorts):
    """Summarize baseline-to-candidate outcomes without combining correctness and completion."""
    baseline = cohorts[0]["report"]["attempts"]
    baseline_by_key = {(record["task_id"], record["attempt"]): record for record in baseline}
    comparisons = []
    for candidate in cohorts[1:]:
        candidate_by_key = {(record["task_id"], record["attempt"]): record
                            for record in candidate["report"]["attempts"]}
        correctness = {"both_accepted": 0, "baseline_only": 0, "candidate_only": 0,
                       "both_rejected": 0, "unknown": 0}
        completion = {"both_completed": 0, "baseline_only": 0, "candidate_only": 0,
                      "neither_completed": 0}
        correct_but_incomplete = {"baseline": 0, "candidate": 0}
        for key, left in baseline_by_key.items():
            right = candidate_by_key[key]
            a, b = left["grader_accepted"], right["grader_accepted"]
            if a is None or b is None:
                correctness["unknown"] += 1
            elif a and b:
                correctness["both_accepted"] += 1
            elif a:
                correctness["baseline_only"] += 1
            elif b:
                correctness["candidate_only"] += 1
            else:
                correctness["both_rejected"] += 1
            done_a, done_b = left["unattended_completed"], right["unattended_completed"]
            if done_a and done_b:
                completion["both_completed"] += 1
            elif done_a:
                completion["baseline_only"] += 1
            elif done_b:
                completion["candidate_only"] += 1
            else:
                completion["neither_completed"] += 1
            correct_but_incomplete["baseline"] += int(a is True and not done_a)
            correct_but_incomplete["candidate"] += int(b is True and not done_b)
        comparisons.append({"candidate": candidate["label"], "attempts": len(baseline_by_key),
                            "correctness": correctness, "unattended_completion": completion,
                            "correct_but_not_completed": correct_but_incomplete})
    return comparisons[0] if len(comparisons) == 1 else comparisons


def _value(record, group, field):
    values = record.get(group)
    return values.get(field) if isinstance(values, dict) else None


def _observed_skill_selection(records):
    values = [record["skill_eval"]["observed_skill_selection"] for record in records
              if isinstance(record.get("skill_eval"), dict)
              and isinstance(record["skill_eval"].get("observed_skill_selection"), str)]
    if not values or any(value == "unknown" for value in values):
        return "unknown"
    return values[0] if len(set(values)) == 1 else "mixed"


def _percentile(values, quantile):
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def _aggregate_metric_totals(records, group, fields, expected_count):
    result = {}
    for field in fields:
        observed = [_value(record, group, field) for record in records]
        result[field] = (sum(observed) if len(observed) == expected_count and observed
                         and all(value is not None for value in observed) else None)
    return result


def _aggregate_metric_completeness(records, group, fields, expected_count):
    return {field: len(records) == expected_count and bool(records)
            and all(_value(record, group, field) is not None for record in records)
            for field in fields}


def load_observations(path):
    """Read shared JSONL records emitted by GUI, CLI or external grader adapters."""
    result = []
    for line_number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            result.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise ValueError(f"invalid JSONL at line {line_number}") from error
    return result

