from __future__ import annotations

from harness.processes import ChildLifecycle, stop_owned_child


class FakeChild:
    def __init__(self, exit_code: int | None = None) -> None:
        self.exit_code = exit_code

    def poll(self) -> int | None:
        return self.exit_code

    def terminate(self) -> None:
        pass

    def kill_tree(self) -> None:
        pass

    def wait(self, timeout: float | None = None) -> int | None:
        return self.exit_code

    def exit(self, code: int = 1) -> None:
        self.exit_code = code


def test_ready_child_exiting_before_stop_is_unexpected() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="edge", process=child)
    state.mark_ready()
    child.exit()

    assert state.poll().classification == "unexpected_exit"


def test_ready_child_exit_after_harness_stop_is_expected_stop() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="worker", process=child)
    state.mark_ready()
    state.request_stop()
    child.exit()

    result = state.poll()
    assert result.classification == "expected_stop"
    assert result.code == 1


def test_child_that_exited_before_cleanup_remains_unexpected() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="edge", process=child)
    state.mark_ready()
    child.exit()

    result = stop_owned_child(state)

    assert result.classification == "unexpected_exit"
