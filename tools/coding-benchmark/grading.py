import json
import shutil
import sys
import tempfile
import time
from pathlib import Path

from execution import run_command
from task_io import check_integrity, content_hash, inventory, validate_groups


def grader_command(task, verifier, candidate):
    environment = {"BENCHMARK_CANDIDATE_ROOT": str(candidate), "PYTHONDONTWRITEBYTECODE": "1"}
    if task.language == "python":
        return [sys.executable, "-I", str(verifier / "grade.py")], environment
    if task.language == "typescript":
        if task.suite == "repository" and shutil.which("tsc") is None:
            return None, {"status": "unavailable", "error": "typescript_compiler_unavailable"}
        return ["node", "--experimental-strip-types", str(verifier / "grade.ts")], environment
    command = [
        "dotnet", "build", str(verifier / "Grader.csproj"), "-c", "Release", "--nologo", "--verbosity", "quiet",
        f"-p:BenchmarkCandidateRoot={candidate}", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
    ]
    build = run_command(command, verifier, environment, task.verifier_seconds)
    if build["status"] != "completed" or build["exit_code"] != 0:
        return None, build
    return ["dotnet", str(verifier / "bin/Release/net10.0/Grader.dll")], environment


def grade(task, workspace):
    result = {
        "task_id": task.id, "suite": task.suite, "fixture_sha256": task.fixture_hash,
        "environment": "native-local", "reward": 0, "status": "invalid", "groups": [],
    }
    try:
        files = check_integrity(task, workspace)
    except (ValueError, OSError) as error:
        result["error"] = str(error)
        return result
    result["candidate_sha256"] = content_hash(files)
    seed = inventory(task.seed)
    result["changed_sources"] = sorted(
        name for name in set(seed) | set(files) if seed.get(name) != files.get(name))
    with tempfile.TemporaryDirectory(prefix="ru-benchmark-grade-") as temporary:
        started = time.monotonic()
        scratch = Path(temporary)
        candidate, verifier = scratch / "candidate", scratch / "verifier"
        candidate.mkdir()
        for relative, content in files.items():
            target = candidate / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        shutil.copytree(task.path / "tests", verifier, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
        command, environment = grader_command(task, verifier, candidate)
        execution = environment if command is None else run_command(
            command, scratch, environment, max(0, task.verifier_seconds - (time.monotonic() - started)))
        result["execution"] = execution
        if execution["status"] != "completed":
            result["status"] = execution["status"]
            result["reward"] = None if execution["status"] == "unavailable" else 0
            if execution.get("error"):
                result["error"] = execution["error"]
            return result
        if command is None:
            result["status"] = "build_failed"
            return result
        try:
            groups = validate_groups(json.loads(execution["stdout"]), task.groups)
        except (ValueError, TypeError, KeyError) as error:
            result["status"], result["error"] = "verifier_error", str(error)
            return result
        result["groups"] = groups
        is_success = execution["exit_code"] == 0 and all(group["passed"] for group in groups)
        result["reward"], result["status"] = int(is_success), "passed" if is_success else "failed"
        if task.suite != "terminal":
            by_name = {group["name"]: group["passed"] for group in groups}
            result["FAIL_TO_PASS"] = {name: by_name[name] for name in task.fail_to_pass}
            result["PASS_TO_PASS"] = {name: by_name[name] for name in task.pass_to_pass}
    return result


def apply_reference(task, workspace):
    for relative, content in inventory(task.path / "solution/src").items():
        target = workspace / "src" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
