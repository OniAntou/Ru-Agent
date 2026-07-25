from __future__ import annotations

import logging

from ddgs import DDGS

logger = logging.getLogger(__name__)
_SEARCH_TIMEOUT_SECONDS = 15


def perform_web_search(query: str, max_results: int = 3) -> str:
    try:
        import concurrent.futures

        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(DDGS().text, query, max_results=max_results)
            results = future.result(timeout=_SEARCH_TIMEOUT_SECONDS)
    except concurrent.futures.TimeoutError:
        logger.warning("Web search timed out for query: %s", query[:80])
        return "Web search timed out."
    except Exception as e:
        logger.warning("Web search error: %s", e)
        return f"Error during web search: {e}"

    if not results:
        return "No web results found."
    formatted = [
        f"Result {i}:\nTitle: {res.get('title', 'No title')}\nSnippet: {res.get('body', 'No content')}"
        for i, res in enumerate(results, 1)
    ]
    return "\n\n".join(formatted)
