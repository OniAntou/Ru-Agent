"""Builds the Ru documentation site.

The Markdown lives where the code lives (README, ``docs/``, a few package READMEs), so the site
is assembled from a staged copy that keeps the repository layout: every relative link between
the documents keeps working. A link that leaves the staged set, such as a link to source code,
is rewritten to the matching page on GitHub instead of breaking.

    python docs-site/build.py --out site          # stage, then build with MkDocs
    python docs-site/build.py --stage-only        # stage and write the config, no MkDocs

MkDocs and its theme are installed from ``docs-site/requirements.txt``; they are not part of the
project's Python tooling set.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
HERE = Path(__file__).resolve().parent
REPOSITORY = "https://github.com/OniAntou/Ru-Agent"
BRANCH = "dev"

# Documents the site publishes. Open plans and design drafts stay out: they are work in progress.
SINGLE_FILES = [
    "README.md",
    "CONTRIBUTING.md",
    "CONTEXT.md",
    "AGENTS.md",
    "backend-dotnet/RuAgent.Cli/README.md",
    "sdk/ru-agent/README.md",
    "tools/coding-benchmark/README.md",
]
DOC_TREES = ["docs"]
EXCLUDED_PREFIXES = ("docs/plans/", "docs/superpowers/", "docs/operations/fixtures/")
# Referenced by the documents but not Markdown: copied so images and data links resolve.
EXTRA_FILES = ["backend-dotnet/public-route-contract.json"]

LINK = re.compile(r"(?<!\!)\]\(([^)\s]+?)(\s+\"[^\"]*\")?\)")
IMAGE = re.compile(r"\!\[[^\]]*\]\(([^)\s]+?)\)")


def collect() -> list[Path]:
    files: list[Path] = []
    for name in SINGLE_FILES + EXTRA_FILES:
        path = ROOT / name
        if path.is_file():
            files.append(path)
    for tree in DOC_TREES:
        for path in sorted((ROOT / tree).rglob("*")):
            relative = path.relative_to(ROOT).as_posix()
            if path.is_file() and not relative.startswith(EXCLUDED_PREFIXES):
                files.append(path)
    return files


def github_url(relative: str, is_directory: bool) -> str:
    kind = "tree" if is_directory else "blob"
    return f"{REPOSITORY}/{kind}/{BRANCH}/{relative}"


def rewrite_links(text: str, source: Path, staged: set[str], report: dict[str, int]) -> str:
    def fix(target: str) -> str:
        if re.match(r"^[a-z][a-z0-9+.-]*:", target, flags=re.IGNORECASE) or target.startswith(("#", "/")):
            return target
        path_part, hash_sep, fragment = target.partition("#")
        if not path_part:
            return target
        resolved = (source.parent / path_part).resolve()
        try:
            relative = resolved.relative_to(ROOT).as_posix()
        except ValueError:
            report["outside"] += 1
            return target
        if relative in staged:
            return target
        if resolved.exists():
            report["to_github"] += 1
            return github_url(relative, resolved.is_dir()) + (hash_sep + fragment if fragment else "")
        report["missing"] += 1
        return target

    def link(match: re.Match[str]) -> str:
        return "](" + fix(match.group(1)) + (match.group(2) or "") + ")"

    return LINK.sub(link, text)


def stage(destination: Path) -> dict[str, int]:
    if destination.exists():
        shutil.rmtree(destination)
    files = collect()
    staged = {path.relative_to(ROOT).as_posix() for path in files}
    report = {"files": len(files), "to_github": 0, "missing": 0, "outside": 0}
    for path in files:
        relative = path.relative_to(ROOT)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if path.suffix.lower() == ".md":
            text = path.read_text(encoding="utf-8")
            # GitHub renders Markdown inside a centred <div>; Python-Markdown needs the opt-in.
            text = text.replace('<div align="center">', '<div align="center" markdown="1">')
            target.write_text(rewrite_links(text, path, staged, report), encoding="utf-8", newline="\n")
        else:
            shutil.copy2(path, target)
    return report


def title_of(path: Path) -> str:
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return path.stem


def adr_navigation(staged_root: Path) -> list[dict[str, str]]:
    entries = []
    for path in sorted((staged_root / "docs" / "adr").glob("*.md")):
        entries.append({title_of(path): path.relative_to(staged_root).as_posix()})
    return entries


def navigation(staged_root: Path) -> list:
    def page(title: str, relative: str):
        return {title: relative} if (staged_root / relative).exists() else None

    sections = [
        ("Home", [page("Overview", "README.md")]),
        ("Use Ru", [
            page("Terminal UI and CLI", "backend-dotnet/RuAgent.Cli/README.md"),
            page("Agent tools", "docs/operations/agent-tools.md"),
            page("Subagents", "docs/operations/subagents.md"),
            page("Providers and models", "docs/operations/provider-catalog.md"),
            page("Runtime behavior", "docs/operations/runtime-behavior.md"),
            page("Installing and updating", "docs/operations/distribution-and-updates.md"),
            page("stdio RPC bridge", "docs/operations/rpc.md"),
            page("TypeScript SDK", "sdk/ru-agent/README.md"),
            page("Driving Ru without a provider", "docs/operations/scripted-model.md"),
            page("TUI known limitations", "docs/operations/tui-known-limitations.md"),
        ]),
        ("How it works", [
            page("Architecture", "docs/architecture.md"),
            page("Domain language", "CONTEXT.md"),
            page("Public route contract", "docs/aspnetcore-route-matrix.md"),
            page("Shared workspace coordination", "docs/operations/shared-workspace-coordination.md"),
            page("Logging and diagnostics", "docs/operations/logging.md"),
        ]),
        ("Quality", [
            page("Coding-agent capabilities", "docs/operations/coding-agent-capabilities.md"),
            page("Coding benchmark", "tools/coding-benchmark/README.md"),
            page("Verification log", "docs/operations/verification.md"),
        ]),
        ("Contributing", [
            page("Contributing", "CONTRIBUTING.md"),
            page("Agent guidance", "AGENTS.md"),
            page("Python tooling ownership", "docs/operations/python-dependency-ownership.md"),
            page("Python runtime boundary", "docs/operations/python-runtime-boundary.md"),
        ]),
    ]
    nav: list = []
    for title, pages in sections:
        entries = [entry for entry in pages if entry]
        if entries:
            nav.append({title: entries})
    decisions = adr_navigation(staged_root)
    if decisions:
        nav.append({"Decisions": decisions})
    return nav


def write_config(staged_root: Path, config_path: Path) -> None:
    base = yaml.safe_load((HERE / "mkdocs.yml").read_text(encoding="utf-8"))
    base["docs_dir"] = str(staged_root)
    base["nav"] = navigation(staged_root)
    config_path.write_text(yaml.safe_dump(base, sort_keys=False, allow_unicode=True), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="site", help="directory MkDocs writes the finished site to")
    parser.add_argument("--work", default=str(HERE / ".build"), help="scratch directory for the staged copy")
    parser.add_argument("--stage-only", action="store_true", help="stage the documents and write the config only")
    args = parser.parse_args()

    work = Path(args.work).resolve()
    staged_root = work / "docs"
    report = stage(staged_root)
    config_path = work / "mkdocs.yml"
    write_config(staged_root, config_path)
    print(
        f"staged {report['files']} files; {report['to_github']} links point at GitHub, "
        f"{report['missing']} links target a missing file, {report['outside']} leave the repository"
    )
    if args.stage_only:
        return 0
    out = Path(args.out).resolve()
    result = subprocess.run(
        [sys.executable, "-m", "mkdocs", "build", "--config-file", str(config_path), "--site-dir", str(out)],
        check=False,
    )
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
