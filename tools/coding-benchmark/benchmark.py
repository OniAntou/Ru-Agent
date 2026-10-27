import argparse
import difflib
import hashlib
import json
import os
import shutil
import sys
import tempfile
from datetime import UTC, datetime
from pathlib import Path

from execution import run_command
from grading import apply_reference, grade
from task_io import IGNORED_PARTS, check_integrity, inventory, load_task, tree_hash, write_json

ROOT = Path(__file__).resolve().parent
GIT_ENV = {
    "GIT_AUTHOR_NAME": "Ru Benchmark", "GIT_AUTHOR_EMAIL": "benchmark@local.invalid",
    "GIT_COMMITTER_NAME": "Ru Benchmark", "GIT_COMMITTER_EMAIL": "benchmark@local.invalid",
    "GIT_AUTHOR_DATE": "2026-10-08T00:00:00+00:00", "GIT_COMMITTER_DATE": "2026-10-08T00:00:00+00:00",
}


def tasks(suite, task_root=None):
    directories = {"terminal": "tasks", "swe": "swe-tasks", "everyday": "everyday-tasks", "skills": "skill-tasks"}
    if suite not in directories:
        raise ValueError("unknown suite")
    directory = Path(task_root) if task_root is not None else ROOT / directories[suite]
    return [load_task(path, suite) for path in sorted(directory.iterdir()) if path.is_dir()]


def find_task(suite, task_id):
    for task in tasks(suite):
        if task.id == task_id:
            return task
    raise ValueError("unknown task")


def git(root, *arguments):
    result = run_command(
        ["git", "-c", "core.autocrlf=false", "-c", "core.hooksPath=NUL" if os.name == "nt" else "core.hooksPath=/dev/null",
         "-c", "commit.gpgsign=false", *arguments], root, GIT_ENV, 30,
    )
    if result["status"] != "completed" or result["exit_code"] != 0:
        raise ValueError("git operation failed: " + result["stderr"][:500])
    return result["stdout"]


def prepare(task, destination):
    destination = destination.absolute()
    if destination.exists():
        raise ValueError("trial destination already exists")
    destination.mkdir(parents=True)
    workspace = destination / "workspace"
    shutil.copytree(task.seed, workspace, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
    git(workspace, "init", "--initial-branch=benchmark")
    git(workspace, "add", "--all")
    git(workspace, "commit", "-m", "benchmark baseline")
    trial = {
        "schema_version": 1, "suite": task.suite, "task_id": task.id,
        "workspace": str(workspace), "base_commit": git(workspace, "rev-parse", "HEAD").strip(),
        "base_workspace_sha256": tree_hash(workspace),
        "fixture_sha256": task.fixture_hash, "environment": "native-local",
        "instruction_sha256": hashlib.sha256(task.instruction.encode("utf-8")).hexdigest(),
        "created_utc": datetime.now(UTC).isoformat(),
    }
    if task.suite == "skills":
        trial["skill_eval"] = {
            "scenario": task.skill_scenario,
            "expected_skill_selection": task.expected_skill_selection,
            "observed_skill_selection": "unknown",
        }
    (destination / "instruction.md").write_text(task.instruction, encoding="utf-8")
    write_json(destination / "trial.json", trial)
    return trial


def self_test(selected, attempts):
    if not 1 <= attempts <= 5:
        raise ValueError("self-test attempts must be between one and five")
    results = []
    for task in selected:
        for attempt in range(attempts):
            with tempfile.TemporaryDirectory(prefix="ru-benchmark-oracle-") as temporary:
                trial = prepare(task, Path(temporary) / "trial")
                workspace = Path(trial["workspace"])
                baseline = grade(task, workspace)
                apply_reference(task, workspace)
                oracle = grade(task, workspace)
                valid_seed = baseline["status"] == "failed" and any(not g["passed"] for g in baseline["groups"])
                if task.suite != "terminal":
                    valid_seed = valid_seed and all(not value for value in baseline.get("FAIL_TO_PASS", {}).values())
                    valid_seed = valid_seed and all(baseline.get("PASS_TO_PASS", {}).values())
                results.append({"task_id": task.id, "attempt": attempt + 1, "baseline": baseline, "oracle": oracle,
                                "validated": bool(valid_seed and oracle["status"] == "passed")})
                print(f"{task.id}: seed={baseline['status']}, oracle={oracle['status']}", file=sys.stderr)
    return {"kind": "oracle_self_test", "live_provider": "not_run", "task_quality_pass_rate": None,
            "validated": all(result["validated"] for result in results), "runs": results}


def patch_for(workspace):
    # Patch admission must use the same build/cache exclusions as tree grading,
    # including files the agent has already staged.
    paths = ["src", *(f":(exclude,glob)src/**/{part}/**" for part in sorted(IGNORED_PARTS))]
    git(workspace, "add", "--intent-to-add", "--", *paths)
    return git(workspace, "diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--", *paths)


def export_patch(task, workspace, output):
    check_integrity(task, workspace)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(patch_for(workspace), encoding="utf-8", newline="")
    return {"task_id": task.id, "patch": str(output)}


def grade_patch(task, patch, destination):
    if task.suite not in {"swe", "everyday"}:
        raise ValueError("patch grading requires SWE or Everyday suite")
    if patch.stat().st_size > 512 * 1024:
        raise ValueError("patch size limit")
    trial = prepare(task, destination)
    workspace = Path(trial["workspace"])
    stat = git(workspace, "apply", "--numstat", "--", str(patch.resolve()))
    for line in stat.splitlines():
        relative = line.split("\t")[-1]
        if not task.editable(relative) or ".." in Path(relative).parts:
            raise ValueError("patch edits outside allowed sources")
    content = patch.read_text(encoding="utf-8")
    if "120000" in content or "GIT binary patch" in content:
        raise ValueError("only regular text source patches are admitted")
    git(workspace, "apply", "--check", "--", str(patch.resolve()))
    git(workspace, "apply", "--", str(patch.resolve()))
    result = grade(task, workspace)
    result["base_commit"] = trial["base_commit"]
    return result


def export_swe(output, include_oracle):
    records = []
    for task in tasks("swe"):
        with tempfile.TemporaryDirectory(prefix="ru-benchmark-export-") as temporary:
            trial = prepare(task, Path(temporary) / "trial")
            record = {
                "repo": "ru-benchmark/" + task.id, "instance_id": "ru-benchmark__" + task.id + "-1",
                "base_commit": trial["base_commit"], "problem_statement": task.instruction,
                "version": "1.0", "created_at": "2026-10-08T00:00:00Z", "hints_text": "",
                "FAIL_TO_PASS": json.dumps(task.fail_to_pass), "PASS_TO_PASS": json.dumps(task.pass_to_pass),
                "fixture_sha256": task.fixture_hash, "dataset_kind": "custom_swe_style",
                "patch": "", "test_patch": "",
            }
            if include_oracle:
                workspace = Path(trial["workspace"])
                apply_reference(task, workspace)
                record["patch"] = patch_for(workspace)
                record["test_patch"] = "".join(
                    "".join(difflib.unified_diff([], content.decode("utf-8").splitlines(keepends=True),
                                              fromfile="/dev/null", tofile="b/.benchmark_tests/" + relative))
                    for relative, content in inventory(task.path / "tests").items()
                )
            records.append(record)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records), encoding="utf-8")
    return {"exported": len(records), "oracle_included": include_oracle, "output": str(output)}


def run_live(selected, destination, executable, allow_live, attempts, trust_workspace=False, tool_profile=None):
    if not allow_live:
        raise ValueError("live execution requires --allow-live")
    if not trust_workspace:
        raise ValueError("fresh live trial workspaces require --trust-workspace")
    if not 1 <= attempts <= 5:
        raise ValueError("live attempts must be between one and five")
    from ru_trial import build_agent_command, run_ru

    build_agent_command(executable)
    for task in selected:
        toolchain = {"csharp": "dotnet", "typescript": "node"}.get(task.language)
        if toolchain is not None and shutil.which(toolchain) is None:
            raise ValueError("toolchain_unavailable: " + toolchain)

    results = []
    for task in selected:
        for attempt in range(attempts):
            trial_dir = destination / f"{task.id}-{attempt + 1}"
            trial = prepare(task, trial_dir)
            execution = run_ru(executable, task, Path(trial["workspace"]), trial_dir, trust_workspace=trust_workspace, tool_profile=tool_profile)
            result = grade(task, Path(trial["workspace"]))
            result.update({"agent": execution, "attempt": attempt + 1, "base_commit": trial["base_commit"]})
            if task.suite == "skills":
                result["skill_eval"] = {
                    "scenario": task.skill_scenario,
                    "expected_skill_selection": task.expected_skill_selection,
                    "observed_skill_selection": "unknown",
                }
            if execution["status"] != "completed":
                result["reward"] = 0
                result["status"] = "agent_" + execution["status"]
            patch = patch_for(Path(trial["workspace"]))
            (trial_dir / "prediction.patch").write_text(patch, encoding="utf-8", newline="")
            write_json(trial_dir / "prediction.json", {
                "instance_id": "ru-benchmark__" + task.id + "-1",
                "model_name_or_path": execution.get("provider_model") or "unavailable",
                "model_patch": patch,
            })
            write_json(trial_dir / "result.json", result)
            results.append(result)
    return {"kind": "live_native_trials", "environment": "native-local", "attempts": attempts,
            "success_rate": sum(result["reward"] == 1 for result in results) / len(results) if results else None,
            "cost": None, "runs": results}


def main():
    parser = argparse.ArgumentParser(description="Ru custom Terminal-Bench / SWE-bench-style tooling")
    parser.add_argument("command", choices=["list", "validate", "prepare", "grade", "self-test", "grade-patch", "export-patch", "export-swe", "run-live", "compare", "compare-cohorts"])
    parser.add_argument("--suite", choices=["terminal", "swe", "everyday", "skills", "all"], default="all")
    parser.add_argument("--task")
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--workspace", type=Path)
    parser.add_argument("--patch", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--predictions", type=Path)
    parser.add_argument("--cohorts", type=Path)
    parser.add_argument("--task-store", type=Path)
    parser.add_argument("--task-root", type=Path, help="private task definitions for one explicit suite")
    parser.add_argument("--track", choices=["harness", "product"])
    parser.add_argument("--attempts", type=int, default=1)
    parser.add_argument("--include-oracle", action="store_true")
    parser.add_argument("--allow-live", action="store_true")
    parser.add_argument("--trust-workspace", action="store_true")
    parser.add_argument("--ru-executable", type=Path)
    parser.add_argument("--tool-profile", choices=["coding_v2", "patch_v2", "coding_code_v1"])
    args = parser.parse_args()
    try:
        if args.task_root is not None and args.suite == "all":
            raise ValueError("--task-root requires one explicit --suite")
        if args.command in {"compare", "compare-cohorts"}:
            selected = []
        elif args.suite == "all":
            selected = tasks("terminal") + tasks("swe") + tasks("everyday")
        else:
            selected = tasks(args.suite, args.task_root)
        if args.task:
            selected = [task for task in selected if task.id == args.task]
            if not selected:
                raise ValueError("unknown task")
        if args.command in {"prepare", "grade", "grade-patch", "export-patch"} and len(selected) != 1:
            raise ValueError("choose exactly one --task")
        if args.command in {"prepare", "grade-patch", "run-live"} and args.destination is None:
            raise ValueError("--destination required")
        if args.command in {"grade", "export-patch"} and args.workspace is None:
            raise ValueError("--workspace required")
        if args.command in {"grade-patch", "export-patch"} and args.patch is None:
            raise ValueError("--patch required")
        if args.command in {"list", "validate"}:
            result = {"tasks": [{"id": task.id, "suite": task.suite, "language": task.language,
                                  "fixture_sha256": task.fixture_hash, "groups": len(task.groups),
                                  **({"skill_scenario": task.skill_scenario,
                                     "expected_skill_selection": task.expected_skill_selection,
                                     "observed_skill_selection": "unknown"} if task.suite == "skills" else {})}
                                 for task in selected],
                      "live_provider": "not_run", "task_quality_pass_rate": None}
        elif args.command == "prepare":
            result = prepare(selected[0], args.destination)
        elif args.command == "grade":
            result = grade(selected[0], args.workspace)
        elif args.command == "grade-patch":
            result = grade_patch(selected[0], args.patch, args.destination)
        elif args.command == "export-patch":
            result = export_patch(selected[0], args.workspace, args.patch)
        elif args.command == "self-test":
            result = self_test(selected, args.attempts)
        elif args.command == "export-swe":
            if args.report is None:
                raise ValueError("--report required for JSONL export")
            result = export_swe(args.report, args.include_oracle)
        elif args.command == "compare":
            if args.manifest is None or args.predictions is None or args.report is None or args.task_store is None:
                raise ValueError("compare requires --manifest, --predictions, --task-store and --report")
            from evaluation import compare, load_manifest, load_records
            result = compare(load_manifest(args.manifest, args.task_store), load_records(args.predictions))
        elif args.command == "compare-cohorts":
            if args.cohorts is None or args.track is None or args.task_store is None or args.report is None:
                raise ValueError("compare-cohorts requires --cohorts, --track, --task-store and --report")
            from evaluation import compare_cohorts, load_manifest, load_records
            entries = json.loads(args.cohorts.read_text(encoding="utf-8"))
            if not isinstance(entries, list):
                raise ValueError("cohorts must be an array")
            for entry in entries:
                if not isinstance(entry, dict) or not all(isinstance(entry.get(key), str)
                        for key in ("manifest", "predictions")):
                    raise ValueError("each cohort needs manifest and predictions paths")
                entry["manifest"] = load_manifest(args.cohorts.parent / entry["manifest"], args.task_store)
                entry["records"] = load_records(args.cohorts.parent / entry["predictions"])
            result = compare_cohorts(entries, args.track)
        else:
            result = run_live(selected, args.destination, args.ru_executable, args.allow_live, args.attempts, args.trust_workspace, args.tool_profile)
        if args.report is not None and args.command != "export-swe":
            write_json(args.report, result)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 1 if result.get("validated") is False or result.get("reward") == 0 else 0
    except (ValueError, OSError, KeyError) as error:
        print("benchmark_error: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
