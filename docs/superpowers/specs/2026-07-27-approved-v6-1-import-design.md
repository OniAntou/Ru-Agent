# Approved conversation import into v6.1

## Goal

Import three manually reviewed local conversation pairs into the shortened v6.1
training split without changing the validation or held-out evaluation sets.

## Selected source rows

The source remains data/conversations/approved.jsonl and is never edited.
The importer accepts these approval IDs:

- 5012176f-d8cf-4fd7-bba0-f94bac083b78: greeting repair.
- 0e124abf-c87a-47e6-be7d-4c1b73c02c72: Ru identity plus the user-approved
  here to make anh happy wording.
- 6e7f8d38-3b8d-41e1-b77e-7410c8ab44c4: concise greeting.

The importer rejects the hallucinated response to Hả?, the weaker
Em tên là Ru. Ahah~ variant because its prompt duplicates the retained
identity response, the long greeting because its prompt only differs in
capitalisation from the retained concise greeting, and the exact duplicate
concise greeting.

## Data shape and provenance

Each accepted pair becomes a standard train row with the Ru system prompt,
one user message, the approved response, Vietnamese language metadata,
user_approved_short_reply mode, single_turn band, and user-approved-local
source. The Llama trainer supports this context shape: it requires only that
the final context message be a user message.

The v6.1 authored-corpus builder remains unchanged because its fixed 2,400-row
shape and multi-turn gates apply to authored source generation, not this
post-build user-approved import.

## Merge and audit rules

The import rebuilds the train split from its non-imported rows plus the three
selected rows, so re-running it is idempotent. It refuses duplicate final
prompts against train, validation, or held-out rows and writes files atomically.
It never changes val.jsonl or held_out.jsonl.

The manifest keeps its existing base-corpus fields and gains an approved_import
audit object with source filename, scanned count, accepted and rejected IDs,
accepted count, final train count, and SHA-256 digests of the approved source
and final train file.

## Verification

1. Unit-test selected-row conversion, rejection, duplicate-prompt protection,
   and idempotent re-run using temporary JSONL files.
2. Run the importer against the real source and v6.1 directory; require
   train=1683, validation=360, and held_out=360.
3. Confirm the three final prompts occur once in train and zero times in
   validation/held-out.
4. Run a small training-data encode check so every imported row ends with a
   user message and can be encoded by the Llama trainer.
5. Do not start training; this task prepares data only.
