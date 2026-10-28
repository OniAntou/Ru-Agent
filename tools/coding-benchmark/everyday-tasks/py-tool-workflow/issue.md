Repair `src/prices.py`: `line_total(row)` must multiply integer `price` by
`quantity` (default 1), retaining zero/negative values. `normalize_currency(text)`
must strip whitespace and uppercase the value.

Create the new file `src/reports/giá báo cáo.py`. Expose `summarize(rows)`, which
accepts any iterable of rows, groups by normalized `currency` and sums line totals.
Return a normal dictionary in first-seen currency order. Empty input returns `{}`.
Never mutate input rows. Use the existing functions from `src.prices` rather than
copying their logic. Keep `currency_label` behavior unchanged. No dependencies.

Run `python smoke.py` from the workspace to verify the result. The source path
with spaces and Vietnamese characters is intentional; preserve it exactly.
