def record_id(row):
    return row.get("record_id", "")


def parse_rows(lines):
    rows = [line for line in lines if line.strip()]
    if not rows:
        return []
    header = rows[0].split(",")
    return [dict(zip(header, line.split(","))) for line in rows[1:]]
