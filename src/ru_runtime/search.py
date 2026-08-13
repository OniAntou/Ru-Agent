from __future__ import annotations

import hashlib
import logging
import re
import threading
from concurrent.futures import Future, ThreadPoolExecutor

logger = logging.getLogger(__name__)
_SEARCH_TIMEOUT_SECONDS = 15
_MAX_RESULTS = 5
_TITLE_LIMIT = 200
_BODY_LIMIT = 500
_HTML_TAG_PATTERN = re.compile(r"<[^>]+>")
_WHITESPACE_PATTERN = re.compile(r"\s+")

_SEARCH_EXECUTOR: ThreadPoolExecutor | None = None
_SEARCH_EXECUTOR_MAX_WORKERS = 2
_SEARCH_EXECUTOR_LOCK = threading.Lock()
_SEARCH_IN_FLIGHT = threading.BoundedSemaphore(_SEARCH_EXECUTOR_MAX_WORKERS)
_SEARCH_FUTURES: set[Future[object]] = set()
_SEARCH_EXECUTOR_SHUTTING_DOWN = False


def _search_executor_locked() -> ThreadPoolExecutor:
    global _SEARCH_EXECUTOR
    if _SEARCH_EXECUTOR is None:
        _SEARCH_EXECUTOR = ThreadPoolExecutor(
            max_workers=_SEARCH_EXECUTOR_MAX_WORKERS,
            thread_name_prefix="websearch",
        )
    return _SEARCH_EXECUTOR


def _search_executor() -> ThreadPoolExecutor:
    with _SEARCH_EXECUTOR_LOCK:
        return _search_executor_locked()


def shutdown_search_executor() -> None:
    global _SEARCH_EXECUTOR, _SEARCH_EXECUTOR_SHUTTING_DOWN
    with _SEARCH_EXECUTOR_LOCK:
        executor = _SEARCH_EXECUTOR
        if executor is None:
            return
        _SEARCH_EXECUTOR_SHUTTING_DOWN = True
    try:
        executor.shutdown(wait=False, cancel_futures=True)
    except Exception as error:
        logger.warning("Search executor shutdown failed: %s", error)
    with _SEARCH_EXECUTOR_LOCK:
        if (
            _SEARCH_EXECUTOR is executor
            and _SEARCH_EXECUTOR_SHUTTING_DOWN
            and not _SEARCH_FUTURES
        ):
            _SEARCH_EXECUTOR = None
            _SEARCH_EXECUTOR_SHUTTING_DOWN = False


def _release_search_slot(future: Future[object]) -> None:
    global _SEARCH_EXECUTOR, _SEARCH_EXECUTOR_SHUTTING_DOWN
    _SEARCH_IN_FLIGHT.release()
    with _SEARCH_EXECUTOR_LOCK:
        _SEARCH_FUTURES.discard(future)
        if _SEARCH_EXECUTOR_SHUTTING_DOWN and not _SEARCH_FUTURES:
            _SEARCH_EXECUTOR = None
            _SEARCH_EXECUTOR_SHUTTING_DOWN = False


def _sanitize_web_text(text: str, limit: int) -> str:
    """Strip HTML tags and control characters, collapse whitespace, cap length."""
    if not text:
        return ""
    cleaned = _HTML_TAG_PATTERN.sub(" ", text)
    cleaned = "".join(ch for ch in cleaned if ord(ch) >= 32 or ch == "\t")
    cleaned = _WHITESPACE_PATTERN.sub(" ", cleaned).strip()
    return cleaned[:limit]


def perform_web_search(query: str, max_results: int = 3) -> str:
    if not query or not query.strip():
        return "No search query provided."

    try:
        from ddgs import DDGS
    except ImportError:
        return "Online search is unavailable because DDGS is not installed."

    max_results = max(1, min(int(max_results), _MAX_RESULTS))
    query_hash = hashlib.sha256(query.encode()).hexdigest()[:12]

    if not _SEARCH_IN_FLIGHT.acquire(blocking=False):
        logger.warning("Web search is busy (hash=%s)", query_hash)
        return "Web search is busy. Try again."
    try:
        with _SEARCH_EXECUTOR_LOCK:
            if _SEARCH_EXECUTOR_SHUTTING_DOWN:
                _SEARCH_IN_FLIGHT.release()
                logger.warning("Web search unavailable during shutdown (hash=%s)", query_hash)
                return "Web search is unavailable during shutdown."
            future = _search_executor_locked().submit(
                lambda: DDGS().text(query, max_results=max_results)
            )
            _SEARCH_FUTURES.add(future)
    except Exception as error:
        _SEARCH_IN_FLIGHT.release()
        logger.warning("Web search submit failed (hash=%s): %s", query_hash, error)
        return "An error occurred during web search."
    future.add_done_callback(_release_search_slot)
    try:
        results = future.result(timeout=_SEARCH_TIMEOUT_SECONDS)
    except TimeoutError:
        logger.warning("Web search timed out (hash=%s)", query_hash)
        future.cancel()
        return "Web search timed out."
    except Exception as error:
        logger.warning("Web search error (hash=%s): %s", query_hash, error)
        return "An error occurred during web search."

    if not results:
        return "No web results found."
    formatted = [
        f"Result {i}:\nTitle: {_sanitize_web_text(res.get('title', 'No title'), _TITLE_LIMIT)}\n"
        f"Snippet: {_sanitize_web_text(res.get('body', 'No content'), _BODY_LIMIT)}"
        for i, res in enumerate(results, 1)
    ]
    return "\n\n".join(formatted)
