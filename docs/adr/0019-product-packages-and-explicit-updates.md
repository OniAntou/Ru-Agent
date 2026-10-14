---
status: accepted
date: 2026-10-14
---
# Separate product packages and explicit updates

Ru Desktop and Ru CLI are separate per-user Windows x64 distributions. Each
contains its frontend, the matching self-contained Public Host and the update
library. They share the canonical Ru profile, not installation files. A packaged
client attaches only to a Host reporting the same software version; a mismatch
requires closing the other application. The runtime descriptor schema and the
43-route contract are unchanged.

Inno Setup owns installation, shortcuts, uninstall registration and the CLI's
own user-PATH entry. Application files belong under dedicated product roots;
uninstall does not delete the Ru profile. The installed marker identifies
product, semantic version, architecture and update protocol. Release packaging
stamps all .NET projects together and checks the actual published version.

Updates are user initiated. Stable is the default; Preview includes stable and
prerelease versions. The BCL-only update client reads only official GitHub
releases, selects the newest compatible newer product asset, bounds metadata
and downloads, and verifies GitHub's SHA-256 asset digest. Missing digest,
unexpected origin, unsupported marker and malformed metadata fail closed.
Provider credentials and agent-generated instructions never enter the updater.
This establishes repository-account/TLS trust, not independent publisher signing.
Authenticode signing remains a separate release requirement when a signing
identity is provisioned; an unsigned preview must be labelled accordingly.

An existing authenticated shutdown route accepts `when_idle=true`. The Host
atomically stops admitting requests, verifies no other request is open and no
durable run is nonterminal, then acknowledges before stopping. A busy or unreadable
state reopens admission and denies the update. Clients first require the Host's
maintenance protocol advertisement, so an older Host cannot ignore the query and
stop active runs. Provider/model selection and execution authority are unaffected.

After verification and idle acknowledgement, the calling application waits for
the helper to acknowledge readiness, then exits. A failed/cancelled handoff
revokes its staged request before the helper can run Setup.
A staged Windows PowerShell maintenance helper waits for that exact process,
checks file locks, rechecks the digest and runs the per-user installer. It never
kills another process. Desktop explicitly requests restart; CLI returns to its
existing terminal. The helper is an out-of-process installation handoff, not a
model/tool runtime, bundled interpreter or new execution authority. Windows
PowerShell is already part of the target OS. Concurrent helpers serialize per
user. Failure leaves a status record and installer log; this is not a promise
of transactional rollback after power loss. Existing installers are the recovery
path, and database downgrade is unsupported.

The initial CLI bootstrap script uses the same official-origin, channel, size
and digest rules before invoking Setup. It is repository installation tooling,
not a dependency of normal CLI/TUI operation. Portable archives use the same
matching frontend/Host tree and marker; source builds update by rebuilding.
