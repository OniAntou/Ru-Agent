def reconcile(rows):
    winners = {}
    order = []
    for row in rows:
        key = row.get("record_id", "").strip().casefold()
        if not key:
            continue
        if key not in winners:
            order.append(key)
            winners[key] = dict(row)
        elif row.get("updated_at", "") >= winners[key].get("updated_at", ""):
            winners[key] = dict(row)
    return [winners[key] for key in order]
