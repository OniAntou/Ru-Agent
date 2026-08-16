from __future__ import annotations


def get_status() -> dict[str, object]:
    return {
        "status": "ok",
        "message": "Backend is running and ready for the WinUI3 UI.",
    }


def get_models(backend: str = "") -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared
    from ru_runtime.generation.runner import BACKEND_KILO, BACKEND_OPENCODE
    from ru_runtime.generation.openai import KILO_GATEWAY_BASE_URL, OPENCODE_BASE_URL

    backend = shared._resolve_backend(backend) or backend
    if backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {"status": "error", "message": f"unsupported backend: {backend!r}"}
    credential = (
        shared.core.opencode_cred
        if backend == BACKEND_OPENCODE
        else shared.core.kilo_cred
    )
    try:
        api_key = credential.get_key() or ""
    except Exception as error:
        shared.logger.warning("Could not read the saved API key: %s", error)
        api_key = ""
    if not api_key:
        return {
            "status": "error",
            "message": "API key is not set. Save it in Model Settings first.",
        }
    base_url = (
        OPENCODE_BASE_URL if backend == BACKEND_OPENCODE else KILO_GATEWAY_BASE_URL
    )
    try:
        from ru_runtime.generation.openai import OpenAIRuntime

        models = OpenAIRuntime.fetch_models(api_key, base_url)
    except Exception as error:
        shared.logger.warning("Failed to fetch models for %s: %s", backend, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok", "models": [model["id"] for model in models]}
