# Connect a provider and run your first task

Ru has no account of its own. You choose a model provider, give it your own API key, open a project
and ask for a change.

## Connect a provider

In the **desktop app**, open **Settings → Models & Providers**:

1. Pick the **Active Provider**.
2. Paste your **API key**. It is stored in Windows Credential Manager and sent only to the provider you
   chose.
3. Select **Test connection** to confirm Ru can reach the provider.
4. Close Settings and pick a model beside the message box.

In the **terminal UI**, use slash commands (type `/` to see them):

```text
/backend          choose the provider
/key <provider>   enter its API key (masked; never printed or logged)
/model            choose a model from the live catalog
/reasoning        set the reasoning effort, if the model offers levels
```

Providers are OpenAI, Anthropic, a Custom OpenAI-compatible server (a keyless endpoint works), and every
provider in the [models.dev](https://models.dev) catalog, listed as `models.dev:<provider-id>`. Models
the catalog marks as free carry a `free` tag in the terminal picker.

> [!NOTE]
> Ru never switches provider, model, key or reasoning effort on its own. If a request fails, the run
> reports it and keeps the selection you made.

## Open a project

- **Desktop:** open your project folder from the sidebar.
- **Terminal:** run `ru` inside the folder, or start a chat for any folder with
  `ru agent chat --workspace PATH`.

The first time Ru sees a folder it asks you to **trust** it. Nothing runs until you decide. Trust is tied
to that folder's identity, so a moved or recreated folder asks again.

## Ask for something small and read-only

Start with a task that cannot change anything:

> Find where this project handles configuration. Explain it before changing anything.

For a first run, choose the `plan` permission mode, which allows reading and searching but denies edits
and terminal commands. In the desktop app, find it under **Settings → General → Permission Preset**; in
the terminal, start with `ru agent chat --preset plan`. [Permissions](permissions.md) explains all three
modes.

## Follow the work

Ru shows what it does as it does it.

- **Desktop:** an activity feed lists each tool call, a changes view shows edited files, and an
  integrated terminal runs beside the chat.
- **Terminal:** each tool call is a row. Press `Ctrl+O` to expand a diff, an error or the model's
  reasoning. Press `Esc` to interrupt a turn.

When Ru needs your permission it stops and asks. Read the command or path in the request before you
approve it.

## Next

- [Permissions and approvals](permissions.md)
- [Using the terminal UI](terminal-ui.md)
- [Teach Ru your project](teach-ru.md)
