from mini_gpt.gui_presentation import (
    append_stream_text,
    conversation_display_segments,
    format_elapsed,
    format_generation_status,
    safe_streamed_response,
)


def test_format_elapsed_is_compact_and_stable():
    assert format_elapsed(0.04) == "under 0.1 s"
    assert format_elapsed(1.24) == "1.2 s"
    assert format_elapsed(61.0) == "1 min 1 s"


def test_generation_status_describes_cache_and_elapsed_time():
    assert format_generation_status(cache_hit=True, seconds=2.6) == "Ru replied in 2.6 s"
    assert (
        format_generation_status(cache_hit=False, seconds=12.2)
        == "Ru replied in 12.2 s (model loaded)"
    )


def test_cloud_status_includes_latency_breakdown():
    assert (
        format_generation_status(
            cache_hit=True,
            seconds=2.4,
            api_seconds=2.1,
            gwen_warm_seconds=0.2,
        )
        == "Ru replied in 2.4 s | API 2.1 s | Gwen warm 0.2 s"
    )


def test_safe_streamed_response_holds_partial_private_markers():
    assert safe_streamed_response("Em chao anh [") == "Em chao anh "
    assert safe_streamed_response("Em chao anh [[VTS:") == "Em chao anh "
    assert safe_streamed_response("Em chao anh [[VTS:vts_04]] Nya~") == "Em chao anh "
    assert safe_streamed_response("[SEAR") == ""
    assert safe_streamed_response("[SEARCH: weather]") == ""


def test_append_stream_text_accepts_deltas_or_cumulative_snapshots():
    text = append_stream_text("", "Ch")
    text = append_stream_text(text, "Chao anh")
    text = append_stream_text(text, "! Em vui qua.")

    assert text == "Chao anh! Em vui qua."
    assert append_stream_text(text, "Chao anh") == "Chao anh! Em vui qua."


def test_conversation_segments_place_user_message_before_ru_reply():
    assert conversation_display_segments("Anh khoe khong?", "Em khoe.") == (
        ("You\n", "user_label"),
        ("Anh khoe khong?\n\n", "user_bubble"),
        ("Ru\n", "ru_label"),
        ("Em khoe.\n\n", "ru_bubble"),
    )
