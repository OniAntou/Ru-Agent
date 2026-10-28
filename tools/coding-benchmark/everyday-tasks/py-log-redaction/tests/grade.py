import importlib
import json
import os
import sys
from pathlib import Path

root = Path(os.environ["BENCHMARK_CANDIDATE_ROOT"])
sys.path.insert(0, str(root))
sanitize = importlib.import_module("src.sanitize").sanitize

def nested():
    value = {"user": {"name": "Ada", "secret": "s"}, "rows": [{"api_key": "k"}]}
    result = sanitize(value)
    return result == {"user": {"name": "Ada", "secret": "[REDACTED]"},
                      "rows": [{"api_key": "[REDACTED]"}]}

def list_and_tuple():
    value = [({"access_token": "x"}, [1, 2])]
    result = sanitize(value)
    return isinstance(result[0], tuple) and result[0][0]["access_token"] == "[REDACTED]"

def normalized_keys():
    return sanitize({" TOKEN ": "x", "Password": "y"}) == {
        " TOKEN ": "[REDACTED]", "Password": "[REDACTED]"}

def safe_types():
    value = [0, False, None, "ok"]
    result = sanitize(value)
    return result == value and result is not value and all(type(actual) is type(expected)
        for actual, expected in zip(result, value, strict=True))

def detached():
    value = {"nested": [{"password": "x"}], "safe": [1]}
    result = sanitize(value)
    result["nested"][0]["password"] = "changed"
    result["safe"].append(2)
    return value == {"nested": [{"password": "x"}], "safe": [1]}

def detached_tuple():
    value = ({"safe": []},)
    result = sanitize(value)
    return isinstance(result, tuple) and result is not value and result[0] is not value[0]

def marker():
    return sanitize({"password": {"deep": "value"}})["password"] == "[REDACTED]"

cases = [("nested_mapping", nested), ("list_values", list_and_tuple),
         ("case_insensitive_keys", normalized_keys), ("preserve_safe_values", safe_types),
         ("no_mutation", detached), ("tuple_shape", detached_tuple),
         ("redaction_marker", marker), ("input_identity", lambda value={"password": "x"}: sanitize(value) is not value)]
groups = []
for name, check in cases:
    try:
        assert check()
        groups.append({"name": name, "passed": True, "checks": 1, "failures": []})
    except Exception as error:
        groups.append({"name": name, "passed": False, "checks": 1, "failures": [type(error).__name__]})
print(json.dumps({"groups": groups}))
sys.exit(0 if all(group["passed"] for group in groups) else 1)
