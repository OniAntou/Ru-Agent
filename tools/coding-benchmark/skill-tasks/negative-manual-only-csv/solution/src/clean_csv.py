from decimal import Decimal, InvalidOperation

def clean_rows(rows):
    result=[]
    for row in rows:
        name=str(row.get("name", "")).strip()
        try: amount=Decimal(str(row.get("amount", "")))
        except (InvalidOperation, ValueError): continue
        if not name or not amount.is_finite(): continue
        result.append({"name":name,"amount":amount})
    return result
