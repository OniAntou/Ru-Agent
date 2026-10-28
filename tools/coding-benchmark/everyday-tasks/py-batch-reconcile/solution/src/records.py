def record_id(row):
    return row.get("record_id", "")


def parse_rows(lines):
    nonblank = [line for line in lines if line.strip()]
    if not nonblank:
        raise ValueError("missing header")
    aliases = {"record id": "record_id", "record_id": "record_id", "updated at": "updated_at"}
    header = [aliases.get(name.strip().casefold(), name.strip().casefold()) for name in nonblank[0].split(",")]
    if not header or any(not name for name in header) or len(set(header)) != len(header):
        raise ValueError("invalid header")
    result = []
    for line in nonblank[1:]:
        values = [value.strip() for value in line.split(",")]
        if len(values) != len(header):
            raise ValueError("row width mismatch")
        result.append(dict(zip(header, values, strict=True)))
    return result
