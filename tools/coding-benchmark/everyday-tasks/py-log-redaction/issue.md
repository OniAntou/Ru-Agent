Implement `sanitize(value)` in `src/sanitize.py` for safe structured logging.
Sensitive mapping keys are `password`, `secret`, `token`, `api_key`, and
`access_token`, matched case-insensitively after trimming surrounding spaces.
Replace the value of a sensitive key with the string `[REDACTED]`.

Recursively process nested dictionaries, lists and tuples. Preserve dictionary
keys, list order and tuple shape. Safe scalar values and their types are
preserved. Return new containers and never mutate any input container; sharing
an unchanged scalar is fine. A sensitive key takes precedence over recursion,
so its entire value is replaced. Do not stringify arbitrary objects, add
dependencies, or change the public function name.
