import importlib
import json
import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, os.environ["BENCHMARK_CANDIDATE_ROOT"])
module = importlib.import_module("src.config")


def raises(kind, action):
    try:
        action()
    except kind:
        return
    raise AssertionError("expected " + kind.__name__)


cases = [
    (
        "defaults_unchanged",
        'with tempfile.TemporaryDirectory() as d:\n p=Path(d)/"cfg.json"; p.write_text(\'{"port":9000}\'); defaults={"port":8000}; result=module.load_config(p,defaults); assert result=={"port":9000}; assert defaults=={"port":8000}',
    ),
    (
        "independent_calls",
        'with tempfile.TemporaryDirectory() as d:\n p=Path(d)/"cfg.json"; p.write_text(\'{"a":2}\'); defaults={"a":1}; first=module.load_config(p,defaults); p.write_text(\'{}\'); second=module.load_config(p,defaults); assert second=={"a":1}; first["a"]=8; assert second["a"]==1',
    ),
    (
        "missing_file_copy",
        'with tempfile.TemporaryDirectory() as d:\n defaults={"a":1}; result=module.load_config(Path(d)/"missing",defaults); assert result==defaults and result is not defaults; result["a"]=3; assert defaults["a"]==1',
    ),
    (
        "overlay_values",
        'with tempfile.TemporaryDirectory() as d:\n p=Path(d)/"cfg.json"; p.write_text(\'{"name":"Đặng","flag":null,"new":0}\',encoding="utf-8"); assert module.load_config(p,{"flag":True,"keep":False})=={"name":"Đặng","flag":None,"new":0,"keep":False}',
    ),
    (
        "invalid_documents",
        'with tempfile.TemporaryDirectory() as d:\n p=Path(d)/"cfg.json"; p.write_text("[]"); raises(ValueError,lambda:module.load_config(p,{})); p.write_text("{"); raises(json.JSONDecodeError,lambda:module.load_config(p,{}))',
    ),
    (
        "empty_and_shallow",
        'with tempfile.TemporaryDirectory() as d:\n p=Path(d)/"cfg.json"; p.write_text("{}"); nested=[]; assert module.load_config(p,{"nested":nested})["nested"] is nested',
    ),
]
groups = []
for name, code in cases:
    try:
        exec(
            code,
            {"module": module, "json": json, "tempfile": tempfile, "Path": Path, "raises": raises},
        )
        groups.append({"name": name, "passed": True, "checks": 1, "failures": []})
    except Exception as error:
        groups.append(
            {
                "name": name,
                "passed": False,
                "checks": 1,
                "failures": [type(error).__name__ + ": " + str(error)],
            }
        )
print(json.dumps({"groups": groups}))
sys.exit(0 if all(g["passed"] for g in groups) else 1)
