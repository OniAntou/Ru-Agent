---
name: Currency rounding
description: Compute exact invoice totals with explicit rounding.
---
Use integer minor units. Validate integers without accepting bool. Scale to major units, then use Decimal ROUND_HALF_UP. Format exactly precision digits (0 through 6). Do not mutate rows. $ARGUMENTS may set precision.
