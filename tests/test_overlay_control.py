from types import SimpleNamespace

from mini_gpt.gui import overlay_process_ids


def test_overlay_process_ids_parses_tasklist_csv_and_ignores_other_processes():
    def fake_run(*_args, **_kwargs):
        return SimpleNamespace(
            stdout='"RuDesktopOverlay.exe","6072","Console","1","12,345 K"\n'
            '"python.exe","1234","Console","1","9,000 K"\n'
        )

    assert overlay_process_ids(run=fake_run) == (6072,)


def test_overlay_process_ids_returns_empty_for_invalid_tasklist_output():
    def fake_run(*_args, **_kwargs):
        return SimpleNamespace(stdout="INFO: No tasks are running which match the specified criteria.")

    assert overlay_process_ids(run=fake_run) == ()
