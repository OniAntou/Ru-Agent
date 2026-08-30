from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class VoiceSelection:
    label: str
    engine_id: str
    profile_id: str


@dataclass(frozen=True, slots=True)
class VoiceServiceConfig:
    engine_id: str
    base_url: str
    host: str
    port: int
    python_path: Path
    server_script: Path


VOICE_SELECTIONS = (
    VoiceSelection("Ru (OmniVoice)", "omnivoice", "ru"),
    VoiceSelection("Ái Vy (OmniVoice)", "omnivoice", "ai_vy"),
)
DEFAULT_VOICE_SELECTION = VOICE_SELECTIONS[0]

_SELECTIONS_BY_LABEL = {selection.label: selection for selection in VOICE_SELECTIONS}
_LEGACY_LABELS = {
    "Ru (Tsukuyomi)": DEFAULT_VOICE_SELECTION.label,
    "Ru (Gwen)": DEFAULT_VOICE_SELECTION.label,
    "Ái Vy (Gwen)": "Ái Vy (OmniVoice)",
    "Ru (OmniVoice — test)": DEFAULT_VOICE_SELECTION.label,
    "Ái Vy (OmniVoice — test)": "Ái Vy (OmniVoice)",
}


def voice_selection_by_label(label: str) -> VoiceSelection:
    try:
        return _SELECTIONS_BY_LABEL[label]
    except KeyError as error:
        raise ValueError(f"Unsupported voice selection: {label!r}") from error


def voice_selection_from_saved(value: object) -> VoiceSelection:
    if not isinstance(value, str):
        return DEFAULT_VOICE_SELECTION
    return _SELECTIONS_BY_LABEL.get(_LEGACY_LABELS.get(value, value), DEFAULT_VOICE_SELECTION)


# OmniVoice service defaults (overridable via environment)
_OMNIVOICE_ENV_DIR = "omnivoice_env"
_OMNIVOICE_SERVER_SCRIPT = "src/ru_runtime/voice/omnivoice_server.py"


def voice_service_config(selection: VoiceSelection, project_root: Path) -> VoiceServiceConfig:
    if selection.engine_id != "omnivoice":
        raise ValueError(f"Unsupported voice engine: {selection.engine_id!r}")
    # Accept both RU_VOICE_* (public) and RU_OMNIVOICE_* (server binding) for compatibility
    host = os.environ.get("RU_VOICE_HOST") or os.environ.get("RU_OMNIVOICE_HOST", "127.0.0.1")
    host = host.strip() if isinstance(host, str) else "127.0.0.1"
    raw_port = os.environ.get("RU_VOICE_PORT") or os.environ.get("RU_OMNIVOICE_PORT", "5052")
    try:
        port = int(str(raw_port).strip())
    except ValueError:
        port = 5052
    # Cross-platform venv layout: Windows uses Scripts/python.exe, POSIX uses bin/python
    if os.name == "nt":
        python_rel = Path(_OMNIVOICE_ENV_DIR) / "Scripts" / "python.exe"
    else:
        python_rel = Path(_OMNIVOICE_ENV_DIR) / "bin" / "python"
        # Fallback: some Windows venvs may still be present on WSL checks
        if not (project_root / python_rel).is_file():
            alt = Path(_OMNIVOICE_ENV_DIR) / "Scripts" / "python.exe"
            if (project_root / alt).is_file():
                python_rel = alt
    return VoiceServiceConfig(
        engine_id=selection.engine_id,
        base_url=f"http://{host}:{port}",
        host=host,
        port=port,
        python_path=project_root / python_rel,
        server_script=project_root / _OMNIVOICE_SERVER_SCRIPT,
    )


def validate_voice_environment(config: VoiceServiceConfig) -> None:
    missing = [
        path
        for path in (config.python_path, config.server_script)
        if not path.is_file()
    ]
    if missing:
        hint = "Scripts/python.exe" if os.name == "nt" else "bin/python"
        raise RuntimeError(
            "Môi trường OmniVoice chưa setup đúng cách, thiếu: "
            + ", ".join(str(path) for path in missing)
            + f". Hãy tạo omnivoice_env ({hint}) và voice/omnivoice_server.py."
        )
