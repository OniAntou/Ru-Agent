from decimal import Decimal
from decimal import Decimal, ROUND_HALF_UP

def invoice_total(lines, precision=2):
    if type(precision) is not int or precision < 0 or precision > 6:
        raise ValueError("precision out of range")
    total = 0
    for line in lines:
        unit, quantity = line.get("unit_minor"), line.get("quantity")
        if type(unit) is not int or type(quantity) is not int or quantity < 0:
            raise ValueError("invalid line")
        total += unit * quantity
    value = Decimal(total).scaleb(-2).quantize(Decimal(1).scaleb(-precision), rounding=ROUND_HALF_UP)
    return f"{value:.{precision}f}"
