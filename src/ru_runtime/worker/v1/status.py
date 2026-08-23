from __future__ import annotations


def get_status() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    runner = shared.core.runner
    if runner.load_error:
        return {"status": "error", "message": runner.load_error}
    return {
        "status": "ok",
        "message": "Backend is running and ready for the WinUI3 UI.",
        "backend": runner.backend,
    }


def get_models(backend: str = "") -> dict[str, object]:
    from ru_runtime.generation.openai import (
        KILO_GATEWAY_BASE_URL,
        LMSTUDIO_BASE_URL,
        OLLAMA_BASE_URL,
        OPENCODE_BASE_URL,
    )
    from ru_runtime.generation.runner import (
        BACKEND_KILO,
        BACKEND_LMSTUDIO,
        BACKEND_OLLAMA,
        BACKEND_OPENCODE,
        OPENAI_COMPATIBLE_BACKENDS,
    )
    from ru_runtime.worker.v1 import runtime_services as shared

    backend = shared._resolve_backend(backend) or backend
    if backend not in OPENAI_COMPATIBLE_BACKENDS:
        return {"status": "error", "message": f"unsupported backend: {backend!r}"}

    settings = shared.load_settings()
    api_key = ""
    if backend == BACKEND_OPENCODE:
        try:
            api_key = shared.core.opencode_cred.get_key() or ""
        except Exception as error:
            shared.logger.warning("Could not read OpenCode key: %s", error)
        if not api_key:
            return {
                "status": "error",
                "message": "API key is not set. Save it in Model Settings first.",
            }
        base_url = OPENCODE_BASE_URL
    elif backend == BACKEND_KILO:
        try:
            api_key = shared.core.kilo_cred.get_key() or ""
        except Exception as error:
            shared.logger.warning("Could not read Kilo key: %s", error)
        if not api_key:
            return {
                "status": "error",
                "message": "API key is not set. Save it in Model Settings first.",
            }
        base_url = KILO_GATEWAY_BASE_URL
    elif backend == BACKEND_OLLAMA:
        base_url = settings.get("ollama_url", OLLAMA_BASE_URL)
        api_key = "ollama"
    elif backend == BACKEND_LMSTUDIO:
        base_url = settings.get("lmstudio_url", LMSTUDIO_BASE_URL)
        api_key = "lm-studio"
    else:
        base_url = settings.get("custom_url", "http://localhost:8000/v1")
        try:
            api_key = shared.core.custom_cred.get_key() or ""
        except Exception as error:
            shared.logger.warning("Could not read Custom key: %s", error)
            api_key = ""

    try:
        from ru_runtime.generation.openai import OpenAIRuntime

        models = OpenAIRuntime.fetch_models(api_key, base_url)
    except Exception as error:
        shared.logger.warning("Failed to fetch models for %s: %s", backend, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok", "models": [model["id"] for model in models]}
