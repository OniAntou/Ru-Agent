"""Guard tracked source and docs against UTF-8 text that was decoded as CP1252 and re-saved."""

from __future__ import annotations

import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

CHECKED_SUFFIXES = (".cs", ".xaml", ".md", ".json", ".ps1", ".py", ".resw", ".csproj", ".props")
SKIPPED_PREFIXES = ("tools/coding-benchmark/",)

# Matches double-encoded lead bytes: U+00E2 U+20AC (punctuation), U+00C2/U+00C3/U+00C4
# plus a continuation (Latin-1/Latin Extended-A), U+00E1 U+00BA/U+00BB (Vietnamese) and
# U+00F0 U+0178 (emoji). Written as escapes so this file does not match itself.
MOJIBAKE_PATTERN = re.compile(
    r"\u00e2\u20ac|\u00c3[\u0080-\u00bf]|\u00c2[\u00a0-\u00bf]"
    r"|\u00c4[\u0080-\u00bf]|\u00e1[\u00ba\u00bb]|\u00f0\u0178"
)


def _tracked_files() -> list[str]:
    result = subprocess.run(
        ["git", "-c", "core.quotepath=off", "ls-files", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return [path for path in result.stdout.decode("utf-8").split("\0") if path]


def _checked_files() -> list[Path]:
    files: list[Path] = []
    for relative in _tracked_files():
        if not relative.endswith(CHECKED_SUFFIXES):
            continue
        if relative.replace("\\", "/").startswith(SKIPPED_PREFIXES):
            continue
        files.append(ROOT / relative)
    return files


def _mojibake_lines(path: Path) -> list[str]:
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        return []
    hits: list[str] = []
    for number, line in enumerate(text.splitlines(), start=1):
        if MOJIBAKE_PATTERN.search(line):
            hits.append(f"{path.relative_to(ROOT).as_posix()}:{number}")
    return hits


def test_tracked_sources_have_no_double_encoded_utf8() -> None:
    hits: list[str] = []
    for path in _checked_files():
        hits.extend(_mojibake_lines(path))
    assert not hits, "Double-encoded UTF-8 (mojibake) found at:\n" + "\n".join(hits)
