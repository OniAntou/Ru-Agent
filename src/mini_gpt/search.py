from __future__ import annotations

import concurrent.futures
import hashlib
import logging

logger = logging.getLogger(__name__)
_SEARCH_TIMEOUT_SECONDS = 15
_search_pool = concurrent.futures.ThreadPoolExecutor(max_workers=2, thread_name_prefix="websearch")


def perform_web_search(query: str, max_results: int = 3) -> str:
    if not query or not query.strip():
        return "No search query provided."

    try:
        from ddgs import DDGS
    except ImportError:
        return "Online search is unavailable because DDGS is not installed."

    query_hash = hashlib.sha256(query.encode()).hexdigest()[:12]

    try:
        future = _search_pool.submit(lambda: DDGS().text(query, max_results=max_results))
        results = future.result(timeout=_SEARCH_TIMEOUT_SECONDS)
    except concurrent.futures.TimeoutError:
        logger.warning("Web search timed out (hash=%s)", query_hash)
        return "Web search timed out."
    except Exception as exc:
        logger.warning("Web search error (hash=%s): %s", query_hash, type(exc).__name__)
        return "An error occurred during web search."

    if not results:
        return "No web results found."
    formatted = [
        f"Result {i}:\nTitle: {res.get('title', 'No title')}\nSnippet: {res.get('body', 'No content')}"
        for i, res in enumerate(results, 1)
    ]
    return "\n\n".join(formatted)
