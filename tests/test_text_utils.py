from ru_runtime.policy.text import safe_streamed_text


def test_safe_stream_holds_complete_and_partial_private_markers() -> None:
    assert safe_streamed_text("Chào anh [[VT") == "Chào anh "
    assert safe_streamed_text("Chào anh [[VTS:vts_01]]") == "Chào anh "
    assert safe_streamed_text("Chào anh [[VOICE_PACE:quick]]") == "Chào anh "
    assert safe_streamed_text("Để em tìm [SEAR") == "Để em tìm "


def test_safe_stream_leaves_normal_brackets_untouched() -> None:
    assert safe_streamed_text("Dùng [value] trong code") == "Dùng [value] trong code"


def test_strip_emojis_and_emoticons() -> None:
    from ru_runtime.policy.text import strip_emojis_and_emoticons

    raw = "Chào anh nha! 😊🥰 Chúc anh ngày mới vui vẻ :D ^^ <3"
    cleaned = strip_emojis_and_emoticons(raw)
    assert cleaned == "Chào anh nha! Chúc anh ngày mới vui vẻ"

    assert strip_emojis_and_emoticons("Được rồi nè :3") == "Được rồi nè"
    assert strip_emojis_and_emoticons("Buồn quá T_T ;-;") == "Buồn quá"
    assert strip_emojis_and_emoticons("Bình thường không có icon") == "Bình thường không có icon"
