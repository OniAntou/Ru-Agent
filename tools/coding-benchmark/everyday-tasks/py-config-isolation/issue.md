# Configuration leaks between calls
Fix `load_config(path, defaults)` in `src/config.py`. It must return a fresh
dictionary: start with a shallow copy of defaults, then overlay top-level JSON
properties if the file exists. Never mutate defaults. A missing file also returns
a fresh shallow copy. Preserve null JSON values and unknown keys. Non-object JSON
raises ValueError; malformed JSON still raises JSONDecodeError. Nested values
need not be deep copied. Run `python smoke.py`. Use the standard library only.
