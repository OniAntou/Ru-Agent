/skill currency-rounding precision=2

Create `src/amounts.py` with `invoice_total(lines, precision=2)`. Each line has integer `unit_minor` and `quantity`; sum exact minor units, reject booleans/non-integers and negative quantities, and return a decimal string rounded half-up to the requested number of decimal places. Preserve the input. The `currency-rounding` workflow is explicitly invoked above; its workflow and supporting examples are in `.agents/skills/currency-rounding/`.

No external packages are required.
