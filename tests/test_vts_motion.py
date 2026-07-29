from mini_gpt.vts_motion import (
    IDLE_SWAY_AMPLITUDE,
    SPEAKING_SWAY_AMPLITUDE,
    OrganicSwayController,
    MicroActionScheduler,
    is_safe_micro_action,
    safe_micro_action_entries,
    sway_value,
)


class FakeRandom:
    def __init__(self, values):
        self.values = iter(values)

    def uniform(self, _minimum, _maximum):
        return next(self.values)


def test_speaking_sway_is_smaller_than_idle_baseline_and_old_peak():
    assert SPEAKING_SWAY_AMPLITUDE == 14.0
    assert IDLE_SWAY_AMPLITUDE == 5.0
    assert abs(sway_value(1.0, speaking=True)) <= 14.0
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
