from mini_gpt.gui_presentation import format_elapsed, format_generation_status


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
