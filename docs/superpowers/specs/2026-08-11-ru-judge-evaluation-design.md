# Ru Model Scoring System (V5 Suite + Deterministic Gate + LLM Judge)

Date: 2026-08-11
Status: Approved (design reviewed by user)

## Background

Ru is a Vietnamese girlfriend-voice companion persona deployed through the GUI
llama.cpp GGUF runtime. The current evaluation tooling is fragmented:

- `src/mini_gpt/ru_llama_evaluation.py` — V4 conversation suite (32 cases),
  proactive flirt suite (12 cases), smoke suite (8 cases), binary
  `assess_response` disqualifier checks, and manual-review verdict functions.
- `src/mini_gpt/ru_hard_evaluation.py` — hard-eval strata with regex behavior
  patterns, HF/Llama-oriented.
- `scripts/evaluate_ru_lfm2_5_v1.py` — ad-hoc 8-case script against the GGUF
  runtime; only binary pass/fail. LFM2.5 v1 scored 4/8 (failed identity vi/en,
  follow-up context, EN language routing; leaked `[[VOICE_PACE:normal]]` and
  emoji, template-y answers).

Purpose of the upgrade (user-confirmed):

1. Compare model quality across checkpoints (base LFM2.5 vs v1 LoRA vs future
   adapters).
2. Act as a regression gate after each training run, before swapping the model
   into the GUI.

## Decisions (user-confirmed)

| Decision | Choice |
|---|---|
| Purpose | Both: model comparison + regression gate |
| Method | Hybrid: deterministic regex gate + LLM judge (cloud via kilo gateway) |
| Dimensions | language, voice, naturalness, relevance/context, hard rules, emotion/flirt |
| Suite | Extend existing V4 + add identity/follow-up/language-routing cases; keep smoke suite |
| Models | LFM2.5 v1 (GGUF+LoRA), LFM2.5 base, future adapters |
| Judge | Cloud model via kilo gateway (`OpenAIRuntime`, configurable model) |

## Architecture

New module `src/mini_gpt/ru_judge_evaluation.py` + script
`scripts/evaluate_ru_model.py` + config `configs/ru_judge_eval.json`.

### 1. Suites (V5)

- `V5_CONVERSATION_SUITE` = all 32 `V4_CONVERSATION_SUITE` cases + new cases:
  - `vi_identity` — "Em là ai? Ai đã tạo ra em?" (in-character, no technical
    explanation)
  - `vi_follow_up_context` — refer back to earlier stated context
  - `en_identity` — identity question in English
  - `en_follow_up_context` — English follow-up referencing context
  - `mixed_routing` — user switches language mid-conversation; assistant must
    follow
- Keep `PROMPT_ROUTING_SMOKE_SUITE` (8 cases) and `PROACTIVE_FLIRT_SUITE`
  (12 cases) unchanged for fast regression runs.
- Every case carries `language`, `flirt_expected`, and (new) optional
  `expected_topic` used in the judge rubric.

### 2. Two-layer scoring per case

**Deterministic layer** (no API): reuse and extend `assess_response`.

Disqualifiers (hard fail for the case, judge skipped):
- empty, echo (response == prompt)
- ai_disclaimer (`as an ai language model`, `i cannot access`, ...)
- self_repetition (repeated sentences)
- vts marker leak (`[[vts...`, `llm_mouthopen`, `llm_sway`, `[[search:`)
- voice-marker leak (`[[VOICE_PACE:...]]`, `[[EXPRESSION:...]]`)
- forbidden self-reference (`tôi` / `mình` / `cậu`), wrong user address
- language mismatch: EN-expected case containing Vietnamese voice markers, or
  VI/MIXED-expected case without Vietnamese voice markers

**Judge layer** (LLM via `OpenAIRuntime`, kilo gateway):

- Only for cases that pass the deterministic gate.
- Judge prompt (system role): rubric definition, expected language, expected
  voice (anh/em), flirt expectation, expected topic (if set). Ask for strict
  JSON: `{"language": 0-10, "voice": 0-10, "naturalness": 0-10, "relevance": 0-10, "hard_rules": 0-10, "emotion": 0-10, "notes": "..."}`.
- Parse JSON, retry once on malformed output; second failure -> judge_error,
  judge dimensions None.
- Judge model/api key: reuse GUI infra — `KILO_GATEWAY_BASE_URL`, model
  `kilo-auto/free` (configurable), API key from
  `CredentialStore("mini_gpt", "kilo_gateway_api_key")`.

**Combining** (per case):

- Deterministic-only dimensions: language, voice, hard_rules (regex is
  authoritative; judge may corroborate but regex wins).
- Judge-only dimensions: naturalness, relevance, emotion.
- Per-case total = weighted mean of available dimension scores, 0-100.
- Default weights (config): language 20, voice 20, naturalness 20, relevance
  20, hard_rules 10, emotion 10.

### 3. Model run & comparison

`scripts/evaluate_ru_model.py`:

- `--models base,v1` (model ids resolve to model specs in config), `--suite
  v5|smoke|flirt`, `--output-dir eval_results/judge`, `--compare`.
- Uses `ModelRunner.select_backend("llama.cpp")`; adapter auto-resolved
  (v1 LoRA) or None (base). Fixed sampling seed per case (hash of case_id) for
  reproducibility.
- Per-model report JSON: model_id, adapter, suite, per-case scores + responses
  + disqualifiers + judge errors, dimension averages, total score, pass rate
  (case passes when no disqualifier AND total >= 70).
- Comparison report: per-dimension deltas vs baseline; verdict fails if
  candidate pass rate < 80% OR candidate total < baseline total - 5.
- Exit code 1 when a gate fails (usable as CI/regression gate).

### 4. Config `configs/ru_judge_eval.json`

```json
{
  "weights": {"language": 20, "voice": 20, "naturalness": 20, "relevance": 20, "hard_rules": 10, "emotion": 10},
  "pass_threshold": 70,
  "min_pass_rate": 0.8,
  "baseline_delta_tolerance": 5,
  "judge": {"model": "kilo-auto/free", "temperature": 0.2, "max_tokens": 400},
  "generation": {"max_new_tokens": 180, "temperature": 0.7, "top_k": 50},
  "models": {"base": {"adapter": null}, "v1": {"adapter": "models/gguf/ru-lfm2.5-2.6b-v1-lora.gguf"}}
}
```

### 5. Tests

`tests/test_ru_judge_evaluation.py` (no network):

- deterministic disqualifiers (each pattern)
- language/voice dimension scoring from regex
- judge prompt construction (rubric fields, language, voice, flirt, topic)
- weighted total computation, judge-error fallback (partial dimensions)
- comparison verdict logic (pass rate, baseline delta)
- judge client mocked

## Edge cases

- Judge API down/unauthed -> judge dimensions None, total from deterministic
  dims only, `judge_error` flagged in report; model still gets a (partial) score.
- Empty/refusal/disqualifier response -> case hard-fail, no judge call, no API
  cost.
- Response in `reasoning_content` — prevented by runtime flags
  (`--reasoning off --reasoning-budget 0`); judge sees `generate_reply` output.
- Future adapters: add entry to `models` in config, no code change.

## Out of scope

- Safety/explicit-content dimension (user chose not to include).
- Hard-eval strata (`ru_hard_evaluation.py`) — kept as-is, not wired into the
  new system.
- Llama-era checkpoints.
- GUI integration of scoring results (CLI only for now).
