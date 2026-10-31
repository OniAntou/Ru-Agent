SENSITIVE_KEYS = {"password", "secret", "token", "api_key", "access_token"}
REDACTED = "[REDACTED]"


def sanitize(value):
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            if isinstance(key, str) and key.strip().casefold() in SENSITIVE_KEYS:
                result[key] = REDACTED
            else:
                result[key] = sanitize(item)
        return result
    if isinstance(value, list):
        return [sanitize(item) for item in value]
    if isinstance(value, tuple):
        return tuple(sanitize(item) for item in value)
    return value
