import fnmatch
import hashlib
import json
import re
import tomllib
from dataclasses import dataclass
from pathlib import Path

MAX_FILES = 512
MAX_FILE_BYTES = 2 * 1024 * 1024
MAX_TREE_BYTES = 8 * 1024 * 1024
IGNORED_PARTS = {".git", "__pycache__", "bin", "obj", "node_modules", ".pytest_cache"}


@dataclass(frozen=True)
class Task:
    path: Path
    suite: str
    id: str
    language: str
    seed: Path
    instruction: str
    editable_globs: tuple
    groups: tuple
    fail_to_pass: tuple
    pass_to_pass: tuple
    agent_seconds: int
    verifier_seconds: int
    fixture_hash: str

    def editable(self, relative):
        return any(fnmatch.fnmatchcase(relative, pattern) or
                   fnmatch.fnmatchcase(relative, pattern.replace("/**/", "/"))
                   for pattern in self.editable_globs)


def inventory(root):
    result, total = {}, 0
    root = root.resolve(strict=True)
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root)
        if any(part in IGNORED_PARTS for part in relative.parts):
            continue
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError("linked fixture or candidate entry")
        if not path.is_file():
            continue
        size = path.stat().st_size
        total += size
        if size > MAX_FILE_BYTES or total > MAX_TREE_BYTES or len(result) >= MAX_FILES:
            raise ValueError("fixture or candidate size limit")
        result[relative.as_posix()] = path.read_bytes()
    return result


def content_hash(files):
    digest = hashlib.sha256()
    for relative, contents in sorted(files.items()):
        digest.update(relative.encode() + b"\0" + contents + b"\0")
    return digest.hexdigest()


def tree_hash(root):
    return content_hash(inventory(root))


def load_task(path, suite):
    is_terminal = suite == "terminal"
    config_path = path / ("task.toml" if is_terminal else "instance.toml")
    config = tomllib.loads(config_path.read_text(encoding="utf-8-sig"))
    metadata = config.get("metadata", config)
    task_id = metadata["id"]
    if task_id != path.name or not re.fullmatch(r"[a-z][a-z0-9-]{2,80}", task_id):
        raise ValueError("invalid task identity")
    language = metadata["language"]
    if language not in {"csharp", "python", "typescript"}:
        raise ValueError("unsupported task language")
    instruction_path = path / ("instruction.md" if is_terminal else "issue.md")
    seed = path / ("environment/workspace" if is_terminal else "repository")
    fail = tuple(metadata.get("fail_to_pass", []))
    passed = tuple(metadata.get("pass_to_pass", []))
    groups = tuple(metadata["check_groups"]) if is_terminal else fail + passed
    if len(groups) < 6 or len(set(groups)) != len(groups) or not all(isinstance(g, str) and g for g in groups):
        raise ValueError("missing or duplicate grader groups")
    if not is_terminal and (not fail or not passed):
        raise ValueError("SWE requires both fail_to_pass and pass_to_pass")
    globs = tuple(metadata["editable_globs"])
    extension = {"csharp": "cs", "python": "py", "typescript": "ts"}[language]
    if globs != (f"src/**/*.{extension}",):
        raise ValueError("task edits must be restricted to its source language")
    inventory(seed)
    inventory(path / "solution/src")
    instruction = instruction_path.read_text(encoding="utf-8")
    if not instruction.strip() or len(instruction.encode()) > 16384:
        raise ValueError("missing or oversized task instruction")
    instruction += (
        "\n\nFinal workspace constraints:\n"
        "Only source files matching " + ", ".join(globs) + " may be changed or added. "
        "All other existing files must remain unchanged, including project files and public smoke tests. "
        "Remove scratch files before finishing; run scratch checks inside the workspace and remove their files afterwards.\n"
    )
    required = ["tests/test.sh", "environment/Dockerfile", "solution/solve.sh"] if is_terminal else []
    grader = "tests/Grader.csproj" if language == "csharp" else f"tests/grade.{extension}"
    for filename in required + [grader]:
        if not (path / filename).is_file():
            raise ValueError(f"missing task file: {filename}")
    return Task(
        path, suite, task_id, language, seed, instruction, globs, groups, fail, passed,
        int(config.get("agent", {}).get("timeout_sec", metadata.get("agent_timeout_sec", 1800))),
        int(config.get("verifier", {}).get("timeout_sec", metadata.get("verifier_timeout_sec", 120))),
        tree_hash(path),
    )


def check_integrity(task, root):
    seed, candidate = inventory(task.seed), inventory(root)
    for relative, original in seed.items():
        if not task.editable(relative) and candidate.get(relative) != original:
            raise ValueError(f"immutable file changed: {relative}")
    for relative in candidate:
        if relative not in seed and not task.editable(relative):
            raise ValueError(f"unexpected candidate file: {relative}")
    return candidate


def validate_groups(document, expected):
    if not isinstance(document, dict):
        raise ValueError("invalid grader document")
    groups = document.get("groups")
    if not isinstance(groups, list) or len(groups) != len(expected):
        raise ValueError("missing grader groups")
    if not all(isinstance(group, dict) and isinstance(group.get("name"), str) for group in groups):
        raise ValueError("invalid grader groups")
    if {g.get("name") for g in groups} != set(expected):
        raise ValueError("unknown or duplicate grader groups")
    for group in groups:
        checks, passed, failures = group.get("checks"), group.get("passed"), group.get("failures")
        if type(checks) is not int or checks <= 0 or type(passed) is not bool or not isinstance(failures, list):
            raise ValueError("invalid grader evidence")
        if passed != (len(failures) == 0):
            raise ValueError("inconsistent grader evidence")
    return groups


def write_json(path, document):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
