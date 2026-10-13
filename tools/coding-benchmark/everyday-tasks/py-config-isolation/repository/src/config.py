import json
from pathlib import Path

def load_config(path, defaults):
    path = Path(path)
    if not path.exists():
        return defaults
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("configuration must be an object")
    defaults.update(document)
    return defaults
