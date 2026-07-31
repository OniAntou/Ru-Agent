import mini_gpt.gui as gui


def test_overlay_process_ids_keeps_only_rudesktopoverlay(monkeypatch):
    class FakeProc:
        def __init__(self, pid, name):
            self.info = {"pid": pid, "name": name}

    monkeypatch.setattr(
        gui.psutil,
        "process_iter",
        lambda *args, **kwargs: iter(
            [
                FakeProc(6072, "RuDesktopOverlay.exe"),
                FakeProc(1234, "python.exe"),
                FakeProc(9999, None),
            ]
        ),
    )

    assert gui.overlay_process_ids() == (6072,)


def test_overlay_process_ids_returns_empty_when_querying_fails(monkeypatch):
    def boom(*args, **kwargs):
        raise RuntimeError("process table unavailable")

    monkeypatch.setattr(gui.psutil, "process_iter", boom)

    assert gui.overlay_process_ids() == ()
