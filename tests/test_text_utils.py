from mini_gpt._text_utils import safe_streamed_text


def test_safe_stream_holds_complete_and_partial_private_markers() -> None:
    assert safe_streamed_text("Chào anh [[VT") == "Chào anh "
    assert safe_streamed_text("Chào anh [[VTS:vts_01]]") == "Chào anh "
    assert safe_streamed_text("Chào anh [[VOICE_PACE:quick]]") == "Chào anh "
    assert safe_streamed_text("Để em tìm [SEAR") == "Để em tìm "


def test_safe_stream_leaves_normal_brackets_untouched() -> None:
    assert safe_streamed_text("Dùng [value] trong code") == "Dùng [value] trong code"
