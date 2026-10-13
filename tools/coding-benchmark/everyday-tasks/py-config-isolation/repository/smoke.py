import tempfile
from pathlib import Path
from src.config import load_config
with tempfile.TemporaryDirectory() as d:
    p=Path(d)/"config.json"; p.write_text('{"port":9000}')
    defaults={"port":8000}
    assert load_config(p,defaults)=={"port":9000}
    assert defaults=={"port":8000}, "defaults were mutated"
print("smoke passed")
