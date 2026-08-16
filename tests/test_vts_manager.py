from ru_runtime.vts.config import MotionConfig
from ru_runtime.vts.manager import VTSManager, reconnect_delay


def test_reconnect_delay_grows_and_caps_at_30_seconds():
    assert reconnect_delay(0) == 5.0
    assert reconnect_delay(1) == 10.0
    assert reconnect_delay(5) == 30.0
    assert reconnect_delay(50) == 30.0


def test_apply_config_stores_config():
    manager = VTSManager()
    config = MotionConfig(sway_amplitude_idle=18.0)
    manager.apply_config(config)
    assert manager.config == config


def test_note_wrappers_update_context_snapshot():
    from ru_runtime.vts.emotion import EmotionTag

    manager = VTSManager()
    manager.note_speaking(True)
    manager.note_generating(True)
    manager.note_emotion(EmotionTag.JOY)
    snap = manager._context.snapshot()
    assert snap.speaking is True
    assert snap.generating is True
    assert snap.emotion == EmotionTag.JOY


def test_current_state_starts_idle_and_reacts_to_context():
    manager = VTSManager()
    assert manager.current_state().value == "idle"
    manager.note_generating(True)
    snap = manager._context.snapshot()
    manager._fsm.update(snap, now=0.5)
    assert manager.current_state().value == "thinking"


def test_apply_config_is_thread_safe_under_load():
    import threading

    manager = VTSManager()
    errors: list[BaseException] = []

    def worker() -> None:
        try:
            for index in range(200):
                manager.apply_config(MotionConfig(sway_amplitude_idle=5.0 + index % 20))
        except BaseException as exc:  # pragma: no cover
            errors.append(exc)

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []
def test_set_mouse_tracking_applies_sensitivity():
    manager = VTSManager()
    manager.set_mouse_tracking(True, 2.0)
    assert manager._mouse_tracking_enabled is True
    assert manager._mouse_eye_tracker._sensitivity == 2.0


def test_last_connect_error_defaults_none():
    assert VTSManager().last_connect_error is None
