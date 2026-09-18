# Driving Ru without a provider

Ru can run its whole pipeline with a **scripted model** instead of a provider. A run then
executes real tools through the real Broker, in the real app, with no API key and no cost.

This exists because a suite can assert almost everything except what the user actually
sees. An interface needs a live model to render a turn, so a defect that only shows up on
screen — a panel that never draws, a row that disappears when a session is reopened — stays
green in every test. The scripted model closes that gap by making the real app drivable.

## Turning it on

Set `RU_SCRIPTED_MODEL` to a JSON file and start the app normally:

```powershell
$env:RU_SCRIPTED_MODEL = "$env:TEMP\ru-scripted-model.json"
```

Nothing else is required, and **unsetting the variable restores the normal provider path**.

The runner fails closed. A path that does not exist, or a file that does not parse, stops
the host at startup with an error naming the variable. It never falls back to a real
provider, because that would quietly spend the user's credit on what was meant to be a
fixture.

## The script

A JSON array of steps. The model returns them in order, one per model step.

```json
[
  { "text": "Em se lap ke hoach.", "tool": { "id": "todo_write", "arguments": { "todos": [
      { "content": "Doc file index.html", "status": "completed" },
      { "content": "Gop 3 file thanh mot", "status": "in_progress" },
      { "content": "Chay thu nghiem", "status": "pending" } ] } } },
  { "text": "Da lap ke hoach xong." }
]
```

| Field | Meaning |
| --- | --- |
| `text` | Assistant text for this step. Optional. |
| `tool` | A tool to call. Optional; **its presence is what makes the step a tool step.** |
| `tool.id` | Tool id, for example `todo_write`, `list`, `read`. Required inside `tool`. |
| `tool.version` | Tool version. Defaults to `1.0.0`. |
| `tool.arguments` | Argument object. Defaults to `{}`. |

A step with a `tool` finishes as tool calls, so the runtime executes it and hands the result
to the next step. A step without one finishes the turn.

Running out of script ends the turn with a short message rather than failing the run, so a
script one step short leaves a finished turn to inspect instead of a crash.

## What it is good for

- Watching a feature render: activity rows, the task list, an approval prompt, a denied call.
- Reproducing a report by hand, then keeping the script as the regression fixture.
- Checking that state survives something the suite cannot simulate — leaving a session,
  restarting the app, reopening a conversation.

## What it is not

It is a development seam, not a product feature. It replaces the model, so it cannot tell
you anything about provider behaviour, streaming, token accounting or reasoning — those
need a real endpoint. Do not ship a build that depends on it, and do not treat a scripted
run as evidence that a provider path works.

## Known limitation

A script whose **second consecutive tool step** follows another tool call can fail the run
as `prompt_contract_invalid`. The step's prompt receipt is composed here and then checked
against the context the runtime built; after a tool result enters the context the two can
disagree, and the coordinator rejects the step rather than running it.

In practice: a script that calls one tool and then answers works, and so does a script with
several consecutive tool calls where none of them returns content the prompt re-renders.
A script that mixes tool kinds across three or more steps may stop after the first call.
The check is the product's, not the fixture's, and loosening it to make a fixture pass would
weaken a real guarantee — so the limitation is recorded instead.

Assertions about the prompt contract itself live in `ScriptedModelReceiptTests`, which
covers both capabilities renderings directly.
