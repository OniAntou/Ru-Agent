# Ru Authored Dialogue Corpus Design

## Purpose

Replace the V3 corpus assembled from 5CD, UltraChat, OASST2, and the Vietnamese
instruction dataset. Its manual sample was dominated by exercises, factual Q&A,
and artificial-assistant disclaimers, so it cannot be used to improve Ru's
natural conversational behavior.

Ru instead uses a project-authored Vietnamese-English conversation corpus. The
corpus teaches tone, turn-taking, and short contextual replies; the local Qwen
base remains responsible for general language knowledge.

## Decision

Three options were assessed:

1. Tighten the external-source filters again. Rejected: two review samples still
   contained numerous non-conversational examples after tightening.
2. Use the Qwen base without fine-tuning. Safe fallback and a required baseline,
   but it does not establish Ru's local conversational style.
3. Create a curated project-authored dialogue corpus. Selected: it targets the
   actual goal and avoids teaching Ru the bad patterns observed in V2/V3 data.

## Conversational voice

In Vietnamese, Ru defaults to `em` for itself and `anh` for the user. It keeps
that form even when the user's wording does not include a reciprocal pronoun.
Ru changes the form only when the user explicitly asks it to, such as “gọi mình
là chị”; it must not infer a relationship or gender on its own. English
conversations use `I` and `you`.

This rule is included both in authored examples and in the V3 system prompt so
the behavior is available before and after adapter training.

## Corpus shape

The tracked authored source contains at least 600 unique examples split
deterministically 90/10. It is balanced as follows:

| Share | Content |
| --- | --- |
| 45% | greeting, daily check-in, gratitude, small talk, feelings, gentle support |
| 25% | two-to-four-turn follow-ups with an earlier Ru reply that matters |
| 20% | Vietnamese, English, and intentional light code-switching |
| 10% | short, everyday help without specialist instruction content |

Every response is one to four ordinary sentences, has no list, URL, code,
template label, AI identity disclaimer, or unsupported claim. The build rejects
duplicates and verifies that both languages and at least 150 contextual records
are present. The manifest identifies every row as `ru_project_seed` under the
project-owned licence and records the source checksum, counts, filters, and
pronoun policy.

## Data flow and checks

1. A pure authored-seed module emits the fixed record schema already consumed by
   the Qwen response-only encoder.
2. The corpus builder validates the authored source, applies only quality gates
   (not broad external scraping), makes a deterministic train/validation split,
   and atomically writes `data/conversations/ru_v3`.
3. Tests cover each pronoun rule, a contextual follow-up, rejection of every
   known bad pattern, manifest provenance, and a representative content audit.
4. A human-readable 24-example stratified audit must pass before any QLoRA
   smoke run. Two clear failures stop the train.
5. Qwen's own 12-prompt baseline is evaluated before training. The adapter is
   adopted only if it is at least as natural as that baseline and passes the
   existing 10/12 conversation gate.

## Boundaries

The authored corpus is not an attempt to teach general facts, coding, web use,
voice, vision, avatar control, or autonomous learning. It never includes saved
user chats unless the user explicitly exports and approves them separately.
