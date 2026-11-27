from __future__ import annotations

import importlib.util
import sys
import types
from pathlib import Path
from unittest.mock import patch

import pytest

ROOT = Path(__file__).resolve().parents[1]
BUILD_PATH = ROOT / "docs-site" / "build.py"


def _load_build():
    spec = importlib.util.spec_from_file_location("ru_docs_site_build", BUILD_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    with patch.dict(sys.modules, {"yaml": types.ModuleType("yaml")}):
        spec.loader.exec_module(module)
    return module


def test_main_refuses_repository_root_as_work_directory(monkeypatch):
    build = _load_build()
    monkeypatch.setattr(
        build,
        "stage",
        lambda _destination: pytest.fail("stage must not run for a dangerous work directory"),
    )

    with pytest.raises(ValueError, match="work directory"):
        build.main(["--work", str(ROOT), "--stage-only"])


def test_prepare_refuses_an_existing_unowned_destination(tmp_path):
    build = _load_build()
    work = tmp_path / "work"
    destination = work / "docs"
    destination.mkdir(parents=True)
    sentinel = destination / "keep.txt"
    sentinel.write_text("do not delete", encoding="utf-8")

    with pytest.raises(ValueError, match="does not own"):
        build.prepare_work_directory(work)

    assert sentinel.read_text(encoding="utf-8") == "do not delete"


def test_prepare_accepts_an_owned_destination_and_marks_it(tmp_path):
    build = _load_build()
    work = tmp_path / "work"
    destination = work / "docs"
    destination.mkdir(parents=True)
    (work / ".ru-docs-work").write_text("ru-docs-site\n", encoding="utf-8")

    prepared = build.prepare_work_directory(work)

    assert prepared == destination.resolve()
    assert (work / ".ru-docs-work").exists()


def test_prepare_refuses_a_destination_inside_the_source_docs(tmp_path, monkeypatch):
    build = _load_build()
    repository = tmp_path / "repo"
    source_docs = repository / "docs"
    source_docs.mkdir(parents=True)
    monkeypatch.setattr(build, "ROOT", repository)
    monkeypatch.setattr(build, "HERE", repository / "docs-site")

    with pytest.raises(ValueError, match="source documentation"):
        build.prepare_work_directory(source_docs / "scratch")
