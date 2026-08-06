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
_OMNIVOICE_SERVER_SCRIPT = "src/mini_gpt/omnivoice_tts_server.py"


def voice_service_config(selection: VoiceSelection, project_root: Path) -> VoiceServiceConfig:
    if selection.engine_id != "omnivoice":
        raise ValueError(f"Unsupported voice engine: {selection.engine_id!r}")
    host = os.environ.get("RU_VOICE_HOST", "127.0.0.1")
    try:
        port = int(os.environ.get("RU_VOICE_PORT", "5052"))
    except ValueError:
        port = 5052
    return VoiceServiceConfig(
        engine_id=selection.engine_id,
        base_url=f"http://{host}:{port}",
        host=host,
        port=port,
        python_path=project_root / _OMNIVOICE_ENV_DIR / "Scripts" / "python.exe",
        server_script=project_root / _OMNIVOICE_SERVER_SCRIPT,
    )


def validate_voice_environment(config: VoiceServiceConfig) -> None:
    missing = [
        path
        for path in (config.python_path, config.server_script)
        if not path.is_file()
    ]
    if missing:
        raise RuntimeError(
            "Môi trường OmniVoice chưa setup đúng cách, thiếu: "
            + ", ".join(str(path) for path in missing)
            + ". Hãy tạo omnivoice_env (Scripts/python.exe) và omnivoice_tts_server.py."
        )
