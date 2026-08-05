from __future__ import annotations

from fastapi import APIRouter

from .._server_shared import (
    _create_session_file,
    _load_session_file,
    _session_path,
    _session_id_is_valid,
    _DEFAULT_SESSION_TITLE,
    logger,
    SESSION_DIR,
)

router = APIRouter()


@router.get("/sessions")
def sessions_list():
    session_dir = SESSION_DIR
    if not session_dir.is_dir():
        return {"status": "ok", "sessions": []}
    sessions = []
    for path in session_dir.glob("*.json"):
        data = _load_session_file(path.stem)
        if data is None:
            continue
        sessions.append(
            {
                "id": data["id"],
                "title": data.get("title") or _DEFAULT_SESSION_TITLE,
                "created_at": data.get("created_at"),
                "updated_at": data.get("updated_at"),
                "message_count": len(data.get("messages", [])),
            }
        )
    sessions.sort(key=lambda item: str(item.get("updated_at") or ""), reverse=True)
    return {"status": "ok", "sessions": sessions}


@router.post("/sessions/new")
def session_new():
    from pathlib import Path
    session_id = _create_session_file()
    if session_id is None:
        return {"status": "error", "message": "Không tạo được session."}
    return {
        "status": "ok",
        "session_id": session_id,
        "session": {
            "id": session_id,
            "title": _DEFAULT_SESSION_TITLE,
        },
    }


@router.post("/sessions/{session_id}/activate")
def session_activate(session_id: str):
    data = _load_session_file(session_id)
    if data is None:
        return {"status": "error", "message": "Session không tồn tại."}
    return {
        "status": "ok",
        "session": {
            "id": data["id"],
            "title": data.get("title") or _DEFAULT_SESSION_TITLE,
            "messages": data.get("messages", []),
        },
    }


@router.delete("/sessions/{session_id}")
def session_delete(session_id: str):
    from pathlib import Path
    if not _session_id_is_valid(session_id):
        return {"status": "error", "message": "Session không tồn tại."}
    path = _session_path(session_id)
    try:
        path.unlink()
    except FileNotFoundError:
        return {"status": "error", "message": "Session không tồn tại."}
    except OSError as error:
        logger.error("Could not delete session %s: %s", session_id, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok"}
