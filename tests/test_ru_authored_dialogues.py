from mini_gpt.ru_style import RU_SYSTEM_PROMPT


def test_ru_system_prompt_defaults_to_em_for_anh_and_requires_an_explicit_override():
    assert 'call the user "anh"' in RU_SYSTEM_PROMPT
    assert 'yourself as "em"' in RU_SYSTEM_PROMPT
    assert "explicitly requests" in RU_SYSTEM_PROMPT


def test_authored_dialogues_cover_both_languages_and_contextual_turn_taking():
    from mini_gpt.ru_authored_dialogues import authored_dialogue_records

    records = authored_dialogue_records()

    assert len(records) == 120
    assert {record["language"] for record in records} == {"vi", "en"}
    assert sum(len(record["messages"]) > 2 for record in records) >= 30
    assert {record["source"] for record in records} == {"ru_project_seed"}
    assert {record["license"] for record in records} == {"project-owned"}


def test_authored_responses_are_unique():
    from mini_gpt.ru_authored_dialogues import authored_dialogue_records

    responses = [record["response"].lower() for record in authored_dialogue_records()]

    assert len(responses) == len(set(responses))


def test_authored_vietnamese_records_model_the_default_anh_em_voice():
    from mini_gpt.ru_authored_dialogues import authored_dialogue_records

    vietnamese = [record for record in authored_dialogue_records() if record["language"] == "vi"]

    assert any("anh" in record["response"].lower() and "em" in record["response"].lower() for record in vietnamese)


def test_authored_questions_end_with_question_marks():
    from mini_gpt.ru_authored_dialogues import authored_dialogue_records

    records = {record["messages"][-1]["content"]: record["response"] for record in authored_dialogue_records()}

    assert records["I am making dinner."].endswith("?")
    assert records["I just finished work."].endswith("?")
    assert records["Just noodles and vegetables."].endswith(".")
    assert records["My friend invited me out this weekend."].endswith(".")
