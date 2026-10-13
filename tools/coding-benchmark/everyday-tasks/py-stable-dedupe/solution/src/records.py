def record_ids(rows, key):
    return [row[key] for row in rows if key in row]

def dedupe_records(rows, key):
    result = []
    seen = set()
    for row in rows:
        if key not in row:
            result.append(dict(row))
            continue
        identity = row[key]
        if identity not in seen:
            seen.add(identity)
            result.append(dict(row))
    return result
