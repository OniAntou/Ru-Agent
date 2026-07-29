from mini_gpt.vts_motion import (
    IDLE_SWAY_AMPLITUDE,
    SPEAKING_SWAY_AMPLITUDE,
    MicroActionScheduler,
    is_safe_micro_action,
    sway_value,
)


class FakeRandom:
    def __init__(self, values):
        self.values = iter(values)

    def uniform(self, _minimum, _maximum):
        return next(self.values)


def test_speaking_sway_is_smaller_than_idle_baseline_and_old_peak():
    assert SPEAKING_SWAY_AMPLITUDE == 8.0
    assert IDLE_SWAY_AMPLITUDE == 5.0
    assert abs(sway_value(1.0, speaking=True)) <= 8.0
    assert abs(sway_value(1.0, speaking=False)) <= 5.0


def test_scheduler_waits_for_a_randomized_interval_and_resets_on_new_session():
    scheduler = MicroActionScheduler(random_source=FakeRandom([20.0, 14.0, 20.0]))

    assert scheduler.update(100.0, speaking=True) is False
    assert scheduler.update(119.9, speaking=True) is False
    assert scheduler.update(120.0, speaking=True) is True
    scheduler.update(121.0, speaking=False)
    assert scheduler.update(122.0, speaking=True) is False
    assert scheduler.next_due_at == 142.0


def test_micro_action_filter_excludes_pout_blush_clothing_and_unknown_motion():
    assert is_safe_micro_action("Blink", "blink.motion3.json") is True
    assert is_safe_micro_action("\u5c3e\u5df4\u4e0a\u7ad6", "tail-up.motion3.json") is True
    assert is_safe_micro_action("^", "\u8868\u60c5\u5634-^.motion3.json") is False
    assert is_safe_micro_action("\u8138\u7ea2\u542f\u7528", "blush.motion3.json") is False
    assert is_safe_micro_action("\u5916\u8863\u542f\u7528", "clothes.motion3.json") is False
    assert is_safe_micro_action("Mystery", "unknown.motion3.json") is False
