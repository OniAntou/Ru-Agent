from __future__ import annotations

import tkinter as tk

import pytest

from mini_gpt.gui import RuApp


def test_streaming_replaces_the_same_tk_text_bubble() -> None:
    try:
        root = tk.Tk()
    except tk.TclError:
        pytest.skip("Tk display is unavailable")
    root.withdraw()
    app = object.__new__(RuApp)
    app.transcript_text = tk.Text(root)
    app._has_empty_state = False
    app._stream_response_anchor = None
    app._stream_raw_response = ""
    try:
        app._begin_streaming_turn("Chào em")
        for delta in ("Ch", "ào", " anh"):
            app._append_stream_delta(delta)
        app._finish_streaming_turn("Chào anh")

        assert app.transcript_text.get("1.0", tk.END) == "You\nChào em\n\nRu\nChào anh\n\n\n"
    finally:
        root.destroy()
