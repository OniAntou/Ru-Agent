from __future__ import annotations

import logging
import os

import uvicorn
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from mini_gpt._server_shared import (
    _application_lifespan,
    _apply_settings_on_startup,
    _shutdown_runtime,
    _DESKTOP_ORIGINS,
    _valid_desktop_token,
    MAX_REQUEST_BODY_BYTES,
    SETTINGS_FILE,
    HISTORY_FILE,
    SESSION_DIR,
    _SCREEN_REACTIONS,
    _MEMORY_QUEUE,
    _TURN_LOCK,
    _MEMORY_TURN_LOCK,
    _GPU_LIFECYCLE,
    core,
    save_settings_to_file,
    apply_saved_settings,
    migrate_legacy_history,
    load_sessions,
    _create_session_file,
    _load_session_file,
    _session_append_messages,
    _session_turns,
    _learn_from_turn,
    _process_memory_turn,
    _start_voice_service,
    _post_voice,
    _DEFAULT_SETTINGS,
    ExtractFact,
    MemoryKind,
    SpeechRecognitionError,
    VOICE_SELECTIONS,
)

from mini_gpt.routers.status import router as status_router
from mini_gpt.routers.observation import router as observation_router
from mini_gpt.routers.settings import router as settings_router
from mini_gpt.routers.sessions import router as sessions_router
from mini_gpt.routers.voice import router as voice_router
from mini_gpt.routers.vts import router as vts_router
from mini_gpt.routers.chat import router as chat_router

logger = logging.getLogger(__name__)


def _build_app() -> FastAPI:
    app = FastAPI(title="MiniGPT Headless Backend", lifespan=_application_lifespan)

    app.add_middleware(
        CORSMiddleware,
        allow_origins=list(_DESKTOP_ORIGINS),
        allow_credentials=False,
        allow_methods=["GET", "POST", "DELETE", "OPTIONS"],
        allow_headers=["Authorization", "Content-Type"],
    )

    @app.middleware("http")
    async def desktop_boundary(request, call_next):
        if request.method != "OPTIONS":
            authorization = request.headers.get("authorization", "")
            candidate = authorization.removeprefix("Bearer ").strip()
            if not _valid_desktop_token(candidate):
                from fastapi.responses import JSONResponse
                return JSONResponse({"detail": "Unauthorized"}, status_code=401)

        content_length = request.headers.get("content-length")
        if content_length:
            try:
                if int(content_length) > MAX_REQUEST_BODY_BYTES:
                    from fastapi.responses import JSONResponse
                    return JSONResponse({"detail": "Request body too large"}, status_code=413)
            except ValueError:
                from fastapi.responses import JSONResponse
                return JSONResponse({"detail": "Invalid Content-Length"}, status_code=400)
        if request.method in {"POST", "PUT", "PATCH"}:
            body = await request.body()
            if len(body) > MAX_REQUEST_BODY_BYTES:
                from fastapi.responses import JSONResponse
                return JSONResponse({"detail": "Request body too large"}, status_code=413)
        return await call_next(request)

    app.include_router(status_router)
    app.include_router(observation_router, prefix="/observation")
    app.include_router(settings_router)
    app.include_router(sessions_router)
    app.include_router(voice_router)
    app.include_router(vts_router)
    app.include_router(chat_router)

    return app


app = _build_app()


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Ru FastAPI backend")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument(
        "--secret-token",
        default="",
        help="Loopback bearer token supplied by the WinUI3 host (also honored via RU_DESKTOP_TOKEN).",
    )
    args = parser.parse_args()
    if args.secret_token:
        os.environ["RU_DESKTOP_TOKEN"] = args.secret_token
    else:
        logger.warning(
            "No --secret-token or RU_DESKTOP_TOKEN set: the desktop API will refuse "
            "every request (auth fails closed). Start the backend through the WinUI3 "
            "host or pass --secret-token to enable access."
        )
    uvicorn.run("mini_gpt.server:app", host=args.host, port=args.port)
