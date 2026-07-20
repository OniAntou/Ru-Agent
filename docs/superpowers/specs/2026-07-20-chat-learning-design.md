# Chat Memory and Curated Learning Data

## Goal

Add a safe first step toward learning from conversations: keep a local chat session in the GUI and save only explicitly approved user/assistant turns as JSONL training examples. The model weights must not change during normal chat.

## Alternatives considered

1. **Approved examples only (recommended).** The user marks a useful response, and only that pair is appended to a local dataset. This keeps bad generations and accidental prompts out of future training.
2. **Save every conversation automatically.** This is simpler, but it collects low-quality, duplicated, private, or incorrect data and makes later fine-tuning harder to control.
3. **Online learning after every message.** This would update weights continuously, but is unsafe for this small experimental model: it can learn errors, overfit recent messages, and become difficult to reproduce. It is explicitly out of scope.

## User experience

- The existing GUI becomes a small chat window with a scrollable transcript.
- `Send` generates a reply using the current session context and appends both messages to the transcript.
- `Mark last response useful` saves only the latest user prompt and assistant response.
- `New chat` clears the in-memory transcript without deleting saved examples.
- The status area reports whether the last example was saved and where.
- Checkpoint and generation controls remain available.

## Data model and storage

Use `D:\LLM\data\conversations\approved.jsonl`, which is local, UTF-8, append-only, and ignored by git. Each line contains one JSON object:

```json
{
  "id": "uuid",
  "created_at_utc": "2026-07-20T12:00:00+00:00",
  "prompt": "User message",
  "response": "Assistant response",
  "checkpoint": "D:\\LLM\\checkpoints\\base-smoke\\latest.pt"
}
```

The store creates its parent directory on first save, writes with `ensure_ascii=False`, and never silently overwrites existing records. The saved file is user data and must not be committed or uploaded without review.

## Context handling

The GUI keeps the current session in memory as ordered user/assistant pairs. For generation, it formats recent pairs as:

```text
User: ...
Assistant: ...
User: latest prompt
Assistant:
```

The existing tokenizer/model context window remains the hard limit; generation continues to use the model's trailing context behavior. The saved example stores the original latest prompt and generated response, not the entire formatted context.

## Boundaries and error handling

- Chat generation remains on the existing worker thread.
- File writes happen only after an explicit mark action.
- A missing or unwritable data path displays an error in the GUI and does not crash the window.
- Clearing a session never deletes approved data.
- This feature does not claim that saved examples have trained the model; a later, explicit fine-tuning command will consume reviewed JSONL data.

## Testing and documentation

- Unit-test JSONL save/read behavior, Unicode Vietnamese text, required fields, and append semantics.
- Unit-test chat prompt formatting and session clearing without opening a Tk window.
- Keep the existing model and GUI smoke tests passing.
- Update `README.md` with the chat command, save location, approval behavior, and the fact that fine-tuning is a separate step.
- Inspect project documents for stale paths before committing.
