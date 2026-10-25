def line_total(row):
    return row["price"] + row.get("quantity", 1)


def normalize_currency(text):
    return text.strip()


def currency_label(text):
    return "Currency: " + text
