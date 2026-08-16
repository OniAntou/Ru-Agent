from ru_runtime.vts.catalog import build_catalog, catalog_instruction, parse_catalog_reply
from ru_runtime.vts.catalog import split_reply_metadata
from ru_runtime.vts.emotion import EmotionTag


def test_catalog_instruction_invites_multiple_compatible_expression_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[
            {"name": "Happy", "hotkeyID": "happy-id"},
            {"name": "Blink", "hotkeyID": "blink-id"},
        ],
        expressions=[{"name": "Smile", "expressionFile": "smile.exp3.json"}],
    )

    instruction = catalog_instruction(catalog)

    assert "multiple compatible" in instruction
    assert "vts_01" in instruction
    assert "expr_01" in instruction


def test_catalog_reply_keeps_multiple_distinct_valid_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[
            {"name": "Happy", "hotkeyID": "happy-id"},
            {"name": "Blink", "hotkeyID": "blink-id"},
        ],
        expressions=[{"name": "Smile", "expressionFile": "smile.exp3.json"}],
    )

    clean, codes = parse_catalog_reply(
        "Hay quá! [[VTS:vts_01,expr_01,vts_02]]",
        catalog,
    )

    assert clean == "Hay quá!"
    assert codes == ("vts_01", "expr_01", "vts_02")


def test_catalog_reply_drops_codes_not_in_this_catalog():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[{"name": "Smile", "expressionFile": "smile.exp3.json"}],
    )

    clean, codes = parse_catalog_reply("Hay quá! [[VTS:vts_99,expr_01]]", catalog)

    assert clean == "Hay quá!"
    assert codes == ()


def test_catalog_reply_rejects_duplicate_or_empty_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[{"name": "Smile", "expressionFile": "smile.exp3.json"}],
    )

    duplicate, duplicate_codes = parse_catalog_reply("Hay quá! [[VTS:vts_01,vts_01]]", catalog)
    assert duplicate == "Hay quá!"
    assert duplicate_codes == ()

    empty, empty_codes = parse_catalog_reply("Hay quá! [[VTS:]]", catalog)
    assert empty == "Hay quá!"
    assert empty_codes == ()


def test_catalog_reply_strips_all_markers_and_keeps_all_valid_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[{"name": "Smile", "expressionFile": "smile.exp3.json"}],
    )

    clean, codes = parse_catalog_reply("[[VTS:vts_01]] và [[VTS:expr_01]] Nya~", catalog)

    assert clean == "và Nya~"
    assert codes == ("vts_01", "expr_01")


def test_catalog_reply_without_a_marker_is_passed_through():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[],
    )

    assert parse_catalog_reply("Chào anh hôm nay thế nào?", catalog) == ("Chào anh hôm nay thế nào?", ())


def test_split_reply_metadata_strips_emo_then_vts_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[],
    )

    clean, codes, emotion = split_reply_metadata(
        "Hay quá! [[EMO:joy]] [[VTS:vts_01]]", catalog
    )

    assert clean == "Hay quá!"
    assert codes == ("vts_01",)
    assert emotion == EmotionTag.JOY


def test_split_reply_metadata_falls_back_to_heuristic():
    clean, codes, emotion = split_reply_metadata("Em buồn quá", None)
    assert clean == "Em buồn quá"
    assert codes == ()
    assert emotion == EmotionTag.SAD
