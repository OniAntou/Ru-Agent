# Security policy

## Reporting a vulnerability

Report privately through GitHub's
[security advisories](https://github.com/OniAntou/Ru-Agent/security/advisories/new)
rather than a public issue. Include the version or commit, the platform, and a
minimal reproduction if you can. Expect an acknowledgement within a few days;
this is a volunteer-run project, so there is no paid response window.

Please do not open a public issue for anything that lets code escape the
permission model, reads credentials or protected paths the active preset does
not authorize, or turns a bounded tool result into unbounded execution or output.

## What is in scope

- The Host's permission model: presets (`plan`, `standard`, `full_access`),
  workspace trust, single-use approvals, durable rules, and the structured-path
  containment checks.
- The local HTTP surface: the bearer-token boundary, the descriptor's
  owner-only permissions, and the `X-Interactive-Confirmed` guardrail.
- The credential and payload protections: Windows Credential Manager storage,
  AES-GCM payload encryption, and fail-closed key handling.
- Tool bounds: timeouts, output caps, cancellation and process-tree cleanup.

## What is not a vulnerability

- **Terminal children run as your Windows user.** There is no OS filesystem or
  network sandbox (see the Full Access warning in the README); a command you
  approve can do anything your account can do. That is the documented design.
- **The local surface is not hardened against your own user account.** Anything
  that can read `%LOCALAPPDATA%\Ru\runtime_descriptor.json` can talk to the
  Host; the boundary is the bearer token in an owner-only descriptor.
- **Prompts and tool results go to the provider you choose.** Data leaving the
  machine on the way to your model provider is expected behaviour, not a leak.

## Supported versions

Fixes land on `dev` and in the published release folder; there are no maintained
back-branches. Rebuild from the latest `dev` before reporting an issue that may
already be fixed.
