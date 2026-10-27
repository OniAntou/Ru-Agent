---
name: CSV audit
description: Clean and validate financial CSV rows.
disable-model-invocation: true
---
Only run when explicitly invoked. Return new row dictionaries; use Decimal, skip blank names and invalid/non-finite amounts.
