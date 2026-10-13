import json
from pathlib import Path

def load_config(path, defaults):
    path = Path(path)
    if not path.exists():
        return dict(defaults)
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("configuration must be an object")
    result = dict(defaults)
    result.update(document)
    return result
