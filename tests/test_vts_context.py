import threading

from mini_gpt.vts_context import MotionContextProvider
from mini_gpt.vts_emotion import EmotionTag


def test_snapshot_defaults_are_safe():
    snap = MotionContextProvider().snapshot()
    assert snap.speaking is False
    assert snap.generating is False
    assert snap.user_typing is False
    assert snap.system_idle is False
    assert snap.emotion == EmotionTag.NONE


def test_setters_update_snapshot():
    provider = MotionContextProvider()
    provider.set_speaking(True)
    provider.set_generating(True)
    provider.set_user_typing(True)
    provider.set_emotion(EmotionTag.JOY)
    snap = provider.snapshot()
    assert snap.speaking and snap.generating and snap.user_typing
    assert snap.emotion == EmotionTag.JOY


def test_emotion_accepts_string_and_coerces_unknown():
    provider = MotionContextProvider()
    provider.set_emotion("sad")
    assert provider.snapshot().emotion == EmotionTag.SAD
    provider.set_emotion("madeup")
    assert provider.snapshot().emotion == EmotionTag.NONE


def test_snapshots_are_immutable_after_set():
    provider = MotionContextProvider()
    provider.set_speaking(True)
    first = provider.snapshot()
    provider.set_speaking(False)
    assert first.speaking is True
    assert provider.snapshot().speaking is False


def test_concurrent_updates_do_not_raise():
    provider = MotionContextProvider()
    errors: list[BaseException] = []

    def worker(value: int) -> None:
        try:
            for _ in range(200):
                provider.set_speaking(value % 2 == 0)
                provider.set_generating(value % 3 == 0)
                provider.set_emotion(EmotionTag.JOY if value % 2 else EmotionTag.SAD)
                provider.snapshot()
        except BaseException as exc:  # pragma: no cover
            errors.append(exc)

    threads = [threading.Thread(target=worker, args=(index,)) for index in range(6)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []