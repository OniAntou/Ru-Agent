# Ru API Key Security and Status Design

**Date:** 2026-07-29
**Status:** Approved for implementation planning

## Goal

Protect Ru's OpenCode API key on Windows, replace plaintext GUI persistence,
and provide reliable key validation, quota/rate-limit visibility, and useful
errors without sending additional chat requests.

## Decisions

- Store the OpenCode API key only in Windows Credential Manager through the
  Python `keyring` package. `data/gui_settings.json` keeps only non-sensitive
  backend, model, and voice preferences.
- On first launch after upgrade, migrate a non-empty legacy `api_key` value
  from `gui_settings.json` into Credential Manager, rewrite the settings file
  without that field, and never write the key to logs, status text, exceptions,
  memory, examples, or diagnostics.
- The UI exposes Set/Replace key, Remove key, and Verify key actions. The key
  entry is populated only for the current edit action and is cleared after a
  successful save; Credential Manager remains the sole source for requests.
- Verify key uses the existing OpenCode `/models` request. It does not issue a
  chat completion or consume generation quota.
- Runtime responses inspect standard HTTP signals where supplied: 401/403 for
  invalid/unauthorized keys, 429 and `Retry-After` for rate limiting, and
  provider quota/credit error text only when the API explicitly supplies it.
  Ru must say quota is unavailable when no balance information is provided.
- Persist only non-sensitive status: validation time, available model count,
  last error category/message after redaction, retry time, and whether quota
  information was supplied.

## Components

`credential_store.py` owns the narrowly scoped Credential Manager adapter:
read, write, delete, and legacy migration. It accepts a service name and fixed
account name, returns no secrets in `repr`, and maps an unavailable credential
backend to a user-actionable local error.

`api_key_status.py` owns immutable status/error categories and conversion from
OpenCode HTTP responses/errors. It redacts a known current key and common key
prefixes before any message reaches the GUI or persisted status.

`openai_runtime.py` receives the key explicitly from the credential store at
request time. It preserves existing streaming behavior, but raises typed
runtime errors with category, retry time, and safe message. The existing
`fetch_models` path becomes the verifier and returns model metadata plus
headers needed for status extraction.

`gui.py` owns settings interaction: migration at startup, credential actions,
and a concise key status row. It never serializes the secret. Before a cloud
reply, it reads the current key; a missing, invalid, or rate-limited key blocks
the request with an actionable message and leaves local backends unaffected.

## Data flow

1. Startup loads non-sensitive settings and migrates any old plaintext key once.
2. A user saves/replaces/removes a key through Credential Manager.
3. Verify calls `/models`; the response updates safe status only.
4. Chat reads the key only when OpenCode is selected, sends it in Authorization,
   and converts failure headers/body into a safe status result.
5. The GUI persists only that safe result and prompts the user to use Settings
   when intervention is required.

## Failure behavior

- Missing key: explain how to add it; do not send a request.
- Credential Manager unavailable: retain no plaintext fallback; explain the
  local prerequisite/failure.
- Invalid or unauthorized key: mark status invalid and direct to Replace key.
- Rate limit: display retry timing when supplied; otherwise explain that the
  provider did not expose a retry time.
- Quota/credit: surface it only when explicitly reported by the provider.
- Network/provider faults: preserve the provider-neutral safe error and do not
  invalidate a previously verified key.

## Verification

Tests cover credential storage and deletion via a fake backend, one-time
plaintext migration, secret redaction, each HTTP error category, Retry-After
parsing, absent quota headers, verifier behavior without chat completion, GUI
settings persistence without `api_key`, and OpenCode request key lookup.

Manual acceptance: save a key, restart the GUI, verify it, make one cloud
reply, remove it, then confirm `gui_settings.json`, logs, memory artifacts, and
approved examples contain no key material.

## Non-goals

- Multiple-key rotation, automatic fallback among providers, billing dashboards,
  or cloud synchronization.
- Estimating provider balance from token usage.
- Sending a background validation request at application startup.
