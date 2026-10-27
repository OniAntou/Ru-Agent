from datetime import datetime, timedelta

def available_slots(events, start, end, duration_minutes):
    if type(duration_minutes) is not int or duration_minutes <= 0: raise ValueError("duration")
    parse=lambda value: datetime.fromisoformat(value.replace("Z", "+00:00"))
    left, right = parse(start), parse(end)
    if left.tzinfo is None or right.tzinfo is None or right < left: raise ValueError("window")
    busy=sorted((parse(e["start"]),parse(e["end"])) for e in events)
    result=[]; cursor=left; step=timedelta(minutes=duration_minutes)
    while cursor + step <= right:
        conflict=next(((a,b) for a,b in busy if a < cursor + step and b > cursor), None)
        if conflict: cursor=max(cursor,conflict[1])
        else: result.append(cursor.isoformat().replace("+00:00","Z")); cursor += step
    return result
