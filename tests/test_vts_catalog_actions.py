from mini_gpt.vts_catalog import build_catalog, catalog_instruction, parse_catalog_reply


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
