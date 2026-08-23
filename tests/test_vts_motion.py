from ru_runtime.vts.motion import (
    IDLE_SWAY_AMPLITUDE,
    SPEAKING_SWAY_AMPLITUDE,
    MicroActionScheduler,
    MouseEyeTracker,
    OrganicSwayController,
    is_safe_micro_action,
    safe_micro_action_entries,
    sway_value,
)


class FakeRandom:
    def __init__(self, values):
        self.values = iter(values)

    def uniform(self, _minimum, _maximum):
        return next(self.values)


def test_sway_amplitudes_match_the_active_policy():
    assert SPEAKING_SWAY_AMPLITUDE == 16.0
    assert IDLE_SWAY_AMPLITUDE == 10.0
    assert abs(sway_value(1.0, speaking=True)) <= 16.0
    assert abs(sway_value(1.0, speaking=False)) <= 10.0


def test_scheduler_waits_for_a_randomized_interval_and_resets_on_new_session():
    from ru_runtime.vts.config import MotionConfig
    from ru_runtime.vts.state import VtsMotionState

    scheduler = MicroActionScheduler(random_source=FakeRandom([20.0, 14.0, 20.0]))
    config = MotionConfig.defaults()

    assert scheduler.update(100.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(119.9, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(120.0, VtsMotionState.SPEAKING, config) is True
    scheduler.update(121.0, VtsMotionState.IDLE, config)
    assert scheduler.update(122.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.next_due_at == 134.0
    scheduler.reset()
    assert scheduler.next_due_at is None


def test_scheduler_suppresses_micro_actions_while_listening_or_thinking():
    from ru_runtime.vts.config import MotionConfig
    from ru_runtime.vts.state import VtsMotionState

    scheduler = MicroActionScheduler(random_source=FakeRandom([1.0, 1.0]))
    config = MotionConfig.defaults()
    scheduler.next_due_at = 0.0  # force due

    assert scheduler.update(1.0, VtsMotionState.LISTENING, config) is False
    assert scheduler.next_due_at is None
    assert scheduler.update(1.0, VtsMotionState.THINKING, config) is False
    assert scheduler.next_due_at is None
    assert scheduler.update(1.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.next_due_at == 2.0


def test_breath_stays_bounded_and_uses_config():
    from ru_runtime.vts.config import MotionConfig
    from ru_runtime.vts.motion import BreathController

    controller = BreathController()
    config = MotionConfig.defaults()
    values = [controller.value(index * 0.1, config) for index in range(300)]
    assert max(abs(value) for value in values) <= config.breath_amplitude + 1e-9


def test_state_amplitude_scale_mapping():
    from ru_runtime.vts.motion import state_amplitude_scale
    from ru_runtime.vts.state import VtsMotionState

    assert state_amplitude_scale(VtsMotionState.SPEAKING) == 1.0
    assert state_amplitude_scale(VtsMotionState.IDLE) == 0.6
    assert state_amplitude_scale(VtsMotionState.THINKING) == 0.3
    assert state_amplitude_scale(VtsMotionState.POST_REPLY) == 0.7


def test_pose_scheduler_fires_once_on_post_reply_entry_and_idle_intervals():
    from ru_runtime.vts.config import MotionConfig
    from ru_runtime.vts.motion import PoseCycleScheduler
    from ru_runtime.vts.state import VtsMotionState

    scheduler = PoseCycleScheduler(random_source=FakeRandom([100.0, 100.0, 100.0]))
    config = MotionConfig.defaults()

    assert scheduler.update(10.0, VtsMotionState.POST_REPLY, config) is True
    assert scheduler.update(10.1, VtsMotionState.POST_REPLY, config) is False
    assert scheduler.update(10.2, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(15.0, VtsMotionState.IDLE, config) is False  # timer set at 10.2
    assert scheduler.update(110.2, VtsMotionState.IDLE, config) is True


def test_micro_action_filter_excludes_pout_blush_clothing_and_unknown_motion():
    assert is_safe_micro_action("Blink", "blink.motion3.json") is True
    assert is_safe_micro_action("\u5c3e\u5df4\u4e0a\u7ad6", "tail-up.motion3.json") is True
    assert is_safe_micro_action("^", "\u8868\u60c5\u5634-^.motion3.json") is False
    assert is_safe_micro_action("\u8138\u7ea2\u542f\u7528", "blush.motion3.json") is False
    assert is_safe_micro_action("\u5916\u8863\u542f\u7528", "clothes.motion3.json") is False
    assert is_safe_micro_action("Mystery", "unknown.motion3.json") is False


def test_safe_micro_action_entries_keeps_only_reversible_motion_entries():
    class Entry:
        def __init__(self, label, identifier):
            self.label = label
            self.identifier = identifier

    entries = [
        Entry("Blink", "blink.motion3.json"),
        Entry("\u5916\u8863\u542f\u7528", "clothes.motion3.json"),
        Entry("Mystery", "unknown.motion3.json"),
    ]

    assert safe_micro_action_entries(entries) == [entries[0]]


def test_layered_sway_stays_bounded_and_is_not_a_single_repeating_wave():
    values = [sway_value(index * 0.1, speaking=True) for index in range(240)]

    assert max(abs(value) for value in values) <= SPEAKING_SWAY_AMPLITUDE
    assert len({round(value, 4) for value in values[:80]}) > 60
    assert values[:20] != values[120:140]


def test_controller_eases_into_and_out_of_speaking_motion():
    controller = OrganicSwayController()
    idle = controller.update(now=0.0, elapsed=0.0, speaking=False)
    target = sway_value(0.05, speaking=True)
    speaking_frame = controller.update(now=0.05, elapsed=0.05, speaking=True)
    stopped_frame = controller.update(now=0.10, elapsed=0.10, speaking=False)

    assert idle == 0.0
    assert abs(speaking_frame) < abs(target)
    assert abs(stopped_frame - speaking_frame) < abs(speaking_frame)


def test_controller_adds_bounded_direction_drift_after_a_short_hold():
    controller = OrganicSwayController()
    values = [
        controller.update(index * 0.05, index * 0.05, speaking=True)
        for index in range(45)
    ]

    assert max(abs(value) for value in values) <= SPEAKING_SWAY_AMPLITUDE
    assert any(
        abs(value - sway_value(index * 0.05, speaking=True)) > 0.05
        for index, value in enumerate(values[20:], start=20)
    )

def _converged(tracker, x, y, steps=400):
    for _ in range(steps):
        tracker.feed_position(x, y)
    return tracker


def test_mouse_sensitivity_scales_eye_and_head():
    t1 = MouseEyeTracker()
    t2 = MouseEyeTracker(sensitivity=2.0)
    _converged(t1, 300, 300)
    _converged(t2, 300, 300)
    ex1, ey1 = t1.read_eye()
    ex2, ey2 = t2.read_eye()
    assert abs(ex2 - ex1 * 2.0) < 1e-6
    assert abs(ey2 - ey1 * 2.0) < 1e-6
    hz1, hy1, hx1 = t1.read_head()
    hz2, hy2, hx2 = t2.read_head()
    assert abs(hz2 - hz1 * 2.0) < 1e-6
    assert abs(hy2 - hy1 * 2.0) < 1e-6
    assert abs(hx2 - hx1 * 2.0) < 1e-6
    assert abs(t2.read_body() - t1.read_body() * 2.0) < 1e-6


def test_set_sensitivity_clamps_to_range():
    t = MouseEyeTracker()
    t.set_sensitivity(0.05)
    assert t._sensitivity == 0.1
    t.set_sensitivity(10.0)
    assert t._sensitivity == 3.0
    t.set_sensitivity(1.5)
    assert t._sensitivity == 1.5
