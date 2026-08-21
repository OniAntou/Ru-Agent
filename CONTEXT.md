# Ru Assistant

Ru is a local Vietnamese-English assistant whose desktop, public host, runtime
processes, persistent conversation state, voice, and VTube embodiment are
maintained in this repository.

## Language

**Public Host**:
The ASP.NET Core process that exclusively owns RuApp's authenticated HTTP and
WebSocket contract.
_Avoid_: Edge, proxy, FastAPI backend

**Runtime Worker**:
The authenticated loopback Python process that contains model or hardware
implementations retained in Python.
_Avoid_: Python backend, upstream server

**Generation Kernel**:
The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

**Voice Engine**:
The isolated OmniVoice service running inside `omnivoice_env`, spawned by the
Runtime Worker as a local HTTP loopback process and returning native WAV audio
over a private lifecycle/TTS contract.
_Avoid_: TTS web server, Flask voice server

**Public Contract**:
The stable authenticated surface of 41 HTTP method/path pairs plus `/chat` and
`/voice/stream`.
_Avoid_: Compatibility routes

**Worker Contract**:
The versioned typed commands, events, ordering, cancellation, limits, errors,
and capabilities exchanged with the Runtime Worker.
_Avoid_: Runtime HTTP relay

**Protected User Data**:
Settings, sessions, history, activity, structured memory, credentials, and
reviewed examples used by the active product. Business records live under
`%LOCALAPPDATA%\Ru`; structured memory stays project-owned under
`data/conversations`. `RU_DATA_ROOT` isolates both for tests.
_Avoid_: Cleanup candidates, generated data

**Retired Local Model Assets**:
Source, configs, scripts, datasets, checkpoints, evaluations, and documents
whose only purpose was in-process conversational-model generation or training.
_Avoid_: Reintroducing local model or training dependencies
