---
title: Ru Agent
template: home.html
hide:
  - navigation
  - toc
---

<h1 class="visually-hidden">Ru Agent operator's manual</h1>

<div class="cover" markdown="1">

<div class="cover__id" markdown="1">

<img class="cover__mark" src="_manual/wordmark.svg" alt="ru agent" width="394" height="53">

<p class="cover__kind">Operator's<br>manual</p>

<p class="cover__lede">A coding agent built for Windows. It reads your project, edits files and runs commands while you watch. You bring your own model provider and API key.</p>

<dl class="cover__meta">
<dt>Edition</dt><dd>Preview</dd>
<dt>Platform</dt><dd>Windows x64</dd>
<dt>Surfaces</dt><dd>Desktop app, terminal UI, RPC</dd>
<dt>Licence</dt><dd>MIT</dd>
<dt>Revision</dt><dd>{{REV}} &middot; {{DATE}}</dd>
</dl>

</div>

<div class="plate" markdown="1">
<div class="plate__head"><span>Procedure 1-1</span><span>Install the CLI</span></div>
<div class="plate__body" markdown="1">

1. Open **Command Prompt**.
2. Run the installer. It downloads, verifies and installs the CLI package; there is no setup wizard.

    ```cmd
    powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1'))) -Channel Preview"
    ```

3. Open a new terminal and run `ru`.

</div>
<p class="plate__warn"><strong>Warning</strong> Ru runs terminal commands as your Windows user, with no sandbox. <a href="docs/operations/agent-tools/">Read the tool policy.</a></p>
<p class="plate__foot">Prefer the desktop app? Both installers are on the <a href="https://github.com/OniAntou/Ru-Agent/releases">Releases page</a>. Building from source: <a href="overview/#quick-start">Quick start</a>.</p>
</div>

</div>

!!! warning "Warning"
    **Terminal commands run as your Windows user, without an OS filesystem or network sandbox.** Standard launch permission does not confine a child process to the workspace. Read the [tool policy](docs/operations/agent-tools.md) before you give Ru a task.

!!! caution "Caution"
    Ru is a Preview. No Stable release is published yet, and the initial installers are unsigned. Local-first does not mean offline: web search and fetch make network requests when used, and model requests go to the provider you choose.

## Permission modes

| Mode | Intended use |
| --- | --- |
| `plan` | Explore and plan with read-only tools. |
| `standard` | Work with the workspace under the standard policy; requests beyond it can require approval. |
| `full_access` | Allow broader tool access; external structured files, arbitrary terminal, and structured protected paths are automatic. |

Workspace trust and explicit approvals are part of the workflow. Instructions and skills guide the agent; they do not grant execution authority.

## What you get

| Part | Specification |
| --- | --- |
| A native Windows app | A WinUI 3 chat surface with an activity feed, file changes and an integrated terminal. |
| A terminal interface | A full-screen TUI with session history, model selection, slash commands and a multiline composer. |
| Your provider and keys | Choose the model and provider in Settings. No Ru account; API keys use Windows Credential Manager. |
| Work you can follow | Read and search files, make edits, run builds and tests, inspect tool activity and track background commands. |
| Reusable workflows | Repository instructions and `SKILL.md` workflows for tasks you repeat. |

## In the terminal

<figure class="figure" markdown="1">

```text
> Delegate a review subagent (agent_spawn, review kind) to review a.py for bugs, wait for it with agent_wait, then summarize in two lines.
◐ subagent agent·893a67 started
✓ subagent agent·893a67 finished  ·  /subagents agent_893a67bd803a12f38d96893f39197927 result
```

<figcaption><strong>Fig. 1</strong> Subagent lines in the terminal UI, from a real run of a review subagent on a one-file project. The strip shortens the id; the hint after a finished child carries the full one.</figcaption>
</figure>

## Where to start

New here: [install Ru](docs/guide/install.md), then [connect a provider and run your first task](docs/guide/first-task.md). Before you let it run commands, read [permissions and approvals](docs/guide/permissions.md). The chapters after *Use Ru* are reference material for people who want the exact contract or want to change Ru itself.

## Contents

{{CONTENTS}}

<p class="colophon">Ru Agent &middot; Operator's manual &middot; Preview &middot; Revision {{REV}} &middot; MIT licence</p>
