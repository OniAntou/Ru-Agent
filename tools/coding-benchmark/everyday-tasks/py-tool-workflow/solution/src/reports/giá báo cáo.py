from src.prices import line_total, normalize_currency


def summarize(rows):
    totals = {}
    for row in rows:
        currency = normalize_currency(row["currency"])
        totals[currency] = totals.get(currency, 0) + line_total(row)
    return totals
