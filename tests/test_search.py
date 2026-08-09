import sys
import threading
import time
import types

import pytest

import mini_gpt.search as search


@pytest.fixture(autouse=True)
def clean_search_executor():
    search.shutdown_search_executor()
    yield
    search.shutdown_search_executor()


def test_timeouts_use_one_bounded_pool_and_recover_after_workers_finish(monkeypatch) -> None:
    release_workers = threading.Event()
    workers_started = threading.Event()
    workers_finished = threading.Event()
    state_lock = threading.Lock()
    worker_ids: set[int] = set()
    finished_count = 0

    class BlockingDDGS:
        def text(self, query, max_results):
            nonlocal finished_count
            with state_lock:
                worker_ids.add(threading.get_ident())
                if len(worker_ids) == search._SEARCH_EXECUTOR_MAX_WORKERS:
                    workers_started.set()
            try:
                assert release_workers.wait(timeout=2)
                return []
            finally:
                with state_lock:
                    finished_count += 1
                    if finished_count == search._SEARCH_EXECUTOR_MAX_WORKERS:
                        workers_finished.set()

    class FastDDGS:
        def text(self, query, max_results):
            return [{"title": "result", "href": "https://example.com", "body": "snippet"}]

    monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=BlockingDDGS))
    monkeypatch.setattr(search, "_SEARCH_TIMEOUT_SECONDS", 0.01)
    started_at = time.monotonic()

    try:
        assert search.perform_web_search("call 1") == "Web search timed out."
        assert time.monotonic() - started_at < 0.2
        assert search.perform_web_search("call 2") == "Web search timed out."
        assert workers_started.wait(timeout=1)
        executor = search._SEARCH_EXECUTOR

        assert search.perform_web_search("call 3") == "Web search is busy. Try again."
        assert len(worker_ids) == search._SEARCH_EXECUTOR_MAX_WORKERS
        assert executor is not None
        assert search._SEARCH_EXECUTOR is executor

        release_workers.set()
        assert workers_finished.wait(timeout=1)

        monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=FastDDGS))
        results = search.perform_web_search("call 4")
        assert "result" in results
        assert search._SEARCH_EXECUTOR is executor
    finally:
        release_workers.set()
        workers_finished.wait(timeout=1)


def test_shutdown_does_not_create_a_replacement_pool_while_a_worker_is_running(
    monkeypatch,
) -> None:
    release_worker = threading.Event()
    worker_started = threading.Event()
    worker_finished = threading.Event()

    class BlockingDDGS:
        def text(self, query, max_results):
            worker_started.set()
            try:
                assert release_worker.wait(timeout=2)
                return []
            finally:
                worker_finished.set()

    class FastDDGS:
        def text(self, query, max_results):
            return [{"title": "result", "href": "https://example.com", "body": "snippet"}]

    monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=BlockingDDGS))
    monkeypatch.setattr(search, "_SEARCH_TIMEOUT_SECONDS", 0.01)

    try:
        assert search.perform_web_search("slow call") == "Web search timed out."
        assert worker_started.wait(timeout=1)
        old_executor = search._SEARCH_EXECUTOR
        search.shutdown_search_executor()

        monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=FastDDGS))
        assert search.perform_web_search("during shutdown") == (
            "Web search is unavailable during shutdown."
        )
        assert search._SEARCH_EXECUTOR is old_executor

        release_worker.set()
        assert worker_finished.wait(timeout=1)
        assert "result" in search.perform_web_search("after shutdown")
    finally:
        release_worker.set()
        worker_finished.wait(timeout=1)
