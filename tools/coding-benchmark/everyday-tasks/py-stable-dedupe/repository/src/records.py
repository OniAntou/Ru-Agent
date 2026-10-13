def record_ids(rows, key):
    return [row[key] for row in rows if key in row]

def dedupe_records(rows, key):
    raise NotImplementedError("implement stable deduplication")
