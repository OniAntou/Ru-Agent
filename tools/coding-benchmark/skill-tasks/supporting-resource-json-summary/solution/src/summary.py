from collections import Counter


def summarize(records):
    aliases = {"NE": "NORTH_EAST", "NORTHEAST": "NORTH_EAST", "S": "SOUTH"}
    counts = Counter()
    total = 0
    count = 0
    for record in records:
        region = str(record["region"]).strip().upper()
        region = aliases.get(region, region)
        amount = record["amount_minor"]
        if type(amount) is not int:
            raise ValueError("amount_minor must be an integer")
        counts[region] += 1
        total += amount
        count += 1
    return {"count": count, "total_minor": total, "by_region": dict(counts)}
