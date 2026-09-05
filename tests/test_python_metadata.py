from __future__ import annotations

import tomllib
from pathlib import Path
from typing import Any, cast

from packaging.specifiers import SpecifierSet
from packaging.version import Version

PROJECT_ROOT = Path(__file__).resolve().parents[1]


def _project_metadata() -> dict[str, Any]:
    with (PROJECT_ROOT / "pyproject.toml").open("rb") as project_file:
        return cast(dict[str, Any], tomllib.load(project_file)["project"])


def test_voice_extra_declares_the_isolated_provider_consumers() -> None:
    optional_dependencies = _project_metadata()["optional-dependencies"]
    voice = optional_dependencies["voice"]

    assert any(requirement.startswith("torch") for requirement in voice)
    assert any(requirement.startswith("torchaudio") for requirement in voice)
    assert any(requirement.startswith("omnivoice") for requirement in voice)
    assert any(requirement.startswith("flask") for requirement in voice)


def test_voice_torch_metadata_accepts_the_verified_cuda_provider_build() -> None:
    voice = _project_metadata()["optional-dependencies"]["voice"]
    torch_requirement = next(requirement for requirement in voice if requirement.startswith("torch"))
    torch_specifier = SpecifierSet(torch_requirement.removeprefix("torch"))

    assert Version("2.8.0+cu128") in torch_specifier
    assert Version("2.7.0+cu128") not in torch_specifier


def test_voice_torchaudio_metadata_stays_on_the_matching_major_minor_line() -> None:
    voice = _project_metadata()["optional-dependencies"]["voice"]
    torchaudio_requirement = next(
        requirement for requirement in voice if requirement.startswith("torchaudio")
    )
    torchaudio_specifier = SpecifierSet(torchaudio_requirement.removeprefix("torchaudio"))

    assert Version("2.8.0+cu128") in torchaudio_specifier
    assert Version("2.9.0+cu128") not in torchaudio_specifier
