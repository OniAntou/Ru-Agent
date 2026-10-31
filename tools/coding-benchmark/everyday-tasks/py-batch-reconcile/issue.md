Implement a batch reconciliation operation across `src/records.py` and
`src/reconcile.py`. Keep the existing `record_id(row)` output unchanged.

`parse_rows(lines)` accepts an iterable of CSV-like strings. The first nonblank
line is a header. Trim and case-fold header names; a header named `record id`
or `record_id` maps to `record_id`, and `updated at` maps to `updated_at`.
Skip blank lines. Each returned row is a new dictionary, and values are trimmed
strings. A missing header or a row with a different number of values raises
`ValueError` without returning partial results.

`reconcile(rows)` accepts any iterable of parsed rows. Normalize record IDs by
trimming and case-folding them. Ignore rows with an empty normalized ID. For
each ID, keep the row with the lexicographically greatest `updated_at` value;
when timestamps tie, keep the later input row. Use the normalized ID only for
matching and preserve the selected row's original values. Return a list of
copied rows in the order each ID first appeared. Never mutate the input rows.
Do not add dependencies or change `record_id`.

The public checks cover header aliases, blank lines, normalized IDs, duplicate
conflict policy, stable first-seen ordering, iterators and preservation. Treat
each requirement as part of the same change; a partial implementation is not
accepted.
