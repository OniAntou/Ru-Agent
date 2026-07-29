from __future__ import annotations

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


def voice_service_config(selection: VoiceSelection, project_root: Path) -> VoiceServiceConfig:
    if selection.engine_id != "omnivoice":
        raise ValueError(f"Unsupported voice engine: {selection.engine_id!r}")
    host, port, environment, script = "127.0.0.1", 5052, "omnivoice_env", "omnivoice_tts_server.py"
    return VoiceServiceConfig(
        engine_id=selection.engine_id,
        base_url=f"http://{host}:{port}",
        host=host,
        port=port,
        python_path=project_root / environment / "Scripts" / "python.exe",
        server_script=project_root / script,
    )
