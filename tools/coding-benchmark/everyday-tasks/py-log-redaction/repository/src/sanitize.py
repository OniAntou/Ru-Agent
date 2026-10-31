def sanitize(value):
    if isinstance(value, dict):
        return {key: ("[REDACTED]" if key == "password" else item) for key, item in value.items()}
    return value
