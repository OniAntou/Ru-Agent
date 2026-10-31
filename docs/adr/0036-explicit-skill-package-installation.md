---
status: accepted
date: 2026-10-28
---

# Explicit pinned GitHub skill package installation

## Context

Local discovery under `.agents/skills` does not install online packages. Users
need a reproducible installation path without granting a model a package manager
or running arbitrary setup scripts during discovery.

## Decision

Expose an authenticated user operation through the existing skills API, Client,
TUI and Customize page. Accept only GitHub tree URLs pinned to a full commit SHA
and a skill subdirectory. The Broker downloads the fixed codeload archive with
redirects disabled, validates paths, sizes and shared skill metadata, and stages
the selected package under the managed user skill root.

Persist source, commit and content hashes. Installation never overwrites an
existing target. An explicit update may replace only an unchanged managed
package from the same repository/subdirectory. Use an exclusive root lock,
staging and rename/rollback. Do not execute package scripts or dependencies.
Discovery and selection never download; model tools cannot call the installer.

## Consequences

Installed files use the ordinary skill catalog and retain its precedence,
metadata and execution boundaries. Private repositories, branch/latest URLs,
automatic updates and dependency installation are unsupported. See
[skill operations](../operations/skills.md#explicit-github-package-installation).
