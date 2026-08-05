from mini_gpt.vts_context import MotionContextSnapshot
from mini_gpt.vts_state import (
    LISTENING_ENTER_IDLE_SECONDS,
    MIN_STATE_DWELL_SECONDS,
    POST_REPLY_DWELL_SECONDS,
    MotionStateMachine,
    VtsMotionState,
)


def _snap(**kwargs):
    return MotionContextSnapshot(**kwargs)


def test_starts_idle_and_stays_idle():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    assert machine.state == VtsMotionState.IDLE
    assert machine.update(_snap(), now=1.0) == VtsMotionState.IDLE


def test_speaking_is_authoritative():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(speaking=True, generating=True, user_typing=True), now=0.1)
    assert machine.state == VtsMotionState.SPEAKING


def test_generating_moves_to_thinking_instantly():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(generating=True), now=0.1)
    assert machine.state == VtsMotionState.THINKING


def test_user_typing_moves_to_listening_with_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.6)
    assert machine.state == VtsMotionState.LISTENING
    machine.update(_snap(), now=0.6 + MIN_STATE_DWELL_SECONDS + 0.01)
    assert machine.state == VtsMotionState.LISTENING  # still inside 6s window


def test_listening_returns_to_idle_after_timeout():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.1)
    machine.update(_snap(user_typing=True), now=1.0)
    machine.update(_snap(), now=1.0 + LISTENING_ENTER_IDLE_SECONDS + 0.01)
    assert machine.state == VtsMotionState.IDLE


def test_speech_stop_enters_post_reply_then_idle_after_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(speaking=True), now=0.1)
    assert machine.state == VtsMotionState.SPEAKING
    machine.update(_snap(), now=0.61)
    assert machine.state == VtsMotionState.POST_REPLY
    machine.update(_snap(), now=0.61 + POST_REPLY_DWELL_SECONDS + 0.01)
    assert machine.state == VtsMotionState.IDLE


def test_generating_stop_returns_to_idle():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(generating=True), now=0.1)
    machine.update(_snap(), now=0.61)
    assert machine.state == VtsMotionState.IDLE


def test_quick_typing_flap_does_not_leave_idle_before_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.6)
    assert machine.state == VtsMotionState.LISTENING
    machine.update(_snap(), now=0.7)
    assert machine.state == VtsMotionState.LISTENING  # dwell gate