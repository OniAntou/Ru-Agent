# Troubleshooting

Find the message you see, then follow the fix. Messages in code font are the codes Ru prints.

## A run will not start

| You see | What it means | What to do |
| --- | --- | --- |
| `workspace_trust_required` | In a script or `--json` run, the folder is not trusted yet | Answer with `ru agent trust <run-id> --approve`, or start the run again with `--trust-workspace` |
| `full_access_confirmation_required` | `full_access` needs an explicit confirmation | Add `--confirm-full-access`, once you accept that terminal commands run as your Windows user |
| The folder asks to be trusted again | The folder was moved or recreated | Trust is tied to the folder's identity; trust it again |
| A message naming another run and `ru agent cancel <run-id>` | Another run is still using this folder | Cancel or finish that run |

## The model does not answer

| You see | What it means | What to do |
| --- | --- | --- |
| `model_not_in_catalog: <id>` | The provider's catalog does not list that model, but the run continues | If it fails with a 404, choose another with `/model` |
| `provider_quota_exhausted` | The provider reports your quota or credit is used up | Restore credit or limits at the provider, then `/continue` |
| `provider_payment_required` | The provider needs billing or credit | Fix billing with the provider, then `/continue` |
| `no text arrived from the model` | The run finished but no reply reached the terminal | Retry, or switch model with `/model` |
| The provider list is short or the model picker is empty | The catalog could not be loaded | The picker says why; check your connection and key, then reopen it |

A run that stops on a quota or billing problem **pauses** instead of failing. Nothing is retried for you,
and your provider and model stay as you chose them. Use `/continue` after you fix it, or `/release` to
cancel it.

## The terminal UI looks wrong

- **Plain text, no full-screen UI:** the terminal is smaller than 80 by 24, output is redirected, or
  `NO_COLOR` or `TERM=dumb` is set. Use a larger window.
- **A paste is refused:** a prompt is limited to 16,000 characters. Ru refuses a longer
  paste and leaves your draft unchanged.

To see what the terminal UI is doing, set `RU_TUI_DEBUG=1`; it writes a log to `%TEMP%\ru-tui-debug.log`.

## Updating and the background service

- **An update refuses to install:** a run is unfinished or waiting for approval. Finish or cancel it.
- **`host_version_mismatch`:** two different Ru versions are open. Close the other one.
- **Ru's background service stopped:** it stops by itself after 10 minutes with nothing running, and the
  next command starts it again. Set `RU_HOST_IDLE_MINUTES` to change the delay (`0` keeps it running).

## Logs

Ru writes structured diagnostic logs under `%LOCALAPPDATA%\Ru\logs`. They hold event codes and timings,
not prompts or file contents. Attach the relevant lines when you
[report a bug](https://github.com/OniAntou/Ru-Agent/issues/new?template=bug_report.yml).

## Known limits

[TUI known limitations](../operations/tui-known-limitations.md) lists what the terminal UI does not do
yet, and [Logging and diagnostics](../operations/logging.md) explains the logs in depth.
