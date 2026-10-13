# Stable record deduplication
Implement `dedupe_records(rows, key)` in `src/records.py`. Accept an iterable of
dictionaries, retaining the first row for each present key value in input order.
Rows missing the key are all retained; explicit None is a present value and is
deduplicated. Values for the chosen key are strings, integers or None (no boolean
keys). Zero and empty-string IDs must not be treated as missing. Return a new
list containing shallow copies, without mutating the input or its dictionaries.
Keep `record_ids` unchanged. Run `python smoke.py`. Standard library only.
