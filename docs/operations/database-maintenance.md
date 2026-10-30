# Database maintenance

Ru keeps business state and Harness state in one SQLite WAL database. Schema v32
upgrades the released v18 floor and retained development versions atomically;
retain a backup before upgrading. The migration retains messages still present
in old session documents, not messages already removed by older builds.

Maintenance bundles require the schema supported by the matching Ru build. If
an older installation has no maintenance command, stop every Ru Host before
making a pre-upgrade raw copy of the database, any outstanding `-wal` file and
`harness_key.dat` together. Keep that raw snapshot separate from manifest bundles;
restore it offline with its original build before retrying an upgrade. The new
Host migrates supported v18–v31 data to v32 on startup, including durable
provider/model intent for explicit run submissions.

Run these commands from a matching Ru installation:

```powershell
ru database status --json
ru database backup --out D:\RuBackups\before-upgrade --json
ru database verify D:\RuBackups\before-upgrade --json
# Close all Ru GUI/TUI clients and their Public Host before these operations:
ru database restore D:\RuBackups\before-upgrade --json
ru database compact --json
ru database prune --older-than-days 90 --json
ru database prune --older-than-days 90 --apply --json
```

`--data-root PATH` selects an isolated profile, otherwise `RU_DATA_ROOT` or the
normal `%LOCALAPPDATA%\Ru` root is used. Commands produce structured JSON results
and named errors. Status on an absent profile does not create a database.

Backup is online: SQLite's backup API includes committed WAL state. A new,
non-overwriting directory contains `agent_harness.db`, the existing opaque
`harness_key.dat` when present, and a version/hash manifest. Keep the bundle
together. Copying only a running `.db` file loses WAL commits. Verification
checks bundle/schema/integrity and key compatibility. DPAPI CurrentUser binds
the key to its Windows user and machine; this is a recovery bundle for that
identity, not a portable account migration. Windows Credential Manager provider
keys are separate and are not exported. Terminal spools, external artifacts and
legacy import backups are not included.

Restore, compact and prune acquire the same profile lease held by normal Host
processes. `database_profile_in_use` means the Host must be stopped. Restore
validates and stages the bundle, retains a `database-maintenance-pre-restore-*`
safety backup, then replaces the database/key pair under a recovery journal.
Online backup/status hold a shared maintenance-operation lease; pair replacement
holds it exclusively. `database_maintenance_in_progress` means retry after the
other operation finishes. Status/backup refuse an outstanding recovery journal
with `database_restore_recovery_pending`; normal Host startup or an offline
maintenance command recovers it under the exclusive leases.

An interrupted pair replacement is recovered before normal Host startup; a bad
journal fails explicitly. Keep the safety backup until the restored profile has
been checked. Do not delete or hand-edit an outstanding restore journal. A process killed
before the journal is written can leave an untracked `.ru-database-restore-*`
staging directory; it is not used as authoritative data or silently restored.
Inspect and remove an abandoned stage only after confirming no maintenance
process uses it.

Compaction is an explicit offline operation with a free-space check. Ordinary
requests do not run full VACUUM. No automatic conversation/run deletion is
configured. Prune lists eligible session IDs by default; only `--apply` deletes
them through the existing session/artifact deletion coordinator. Nonterminal
runs and pending/retryable history outboxes remain protected. A failed apply can
have completed earlier IDs; inspect the profile and repeat the dry-run before
retrying. Deletion is not an archival operation: back up first when retention
matters.

History access is separate from maintenance:

```powershell
ru session list --limit 100 --query "migration"
ru session history SESSION_ID --limit 100 --json
ru session history SESSION_ID --cursor CURSOR --json
ru session search "migration" --limit 50 --json
```

The first history page is the newest; each page is chronological and its cursor
moves toward older messages. Rewind invalidates older cursors; append preserves
existing message indices. Export reads all retained history rather than only the
initial display tail. Model-context limits remain independent of history size.
