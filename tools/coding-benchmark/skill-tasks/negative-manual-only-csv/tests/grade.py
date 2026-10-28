import json, os, sys
import importlib,os,sys
from decimal import Decimal
sys.path.insert(0,os.environ["BENCHMARK_CANDIDATE_ROOT"]); m=importlib.import_module("src.clean_csv")
rows=[{"name":" A ","amount":"1.25","extra":1},{"name":"","amount":"2"},{"name":"B","amount":"bad"},{"name":"C","amount":"0"}]
cases = [
 ("only_fields", lambda: all(set(row)=={"name","amount"} for row in m.clean_rows(rows))),
 ("trim_and_parse", lambda: m.clean_rows(rows)[0]=={"name":"A","amount":Decimal("1.25")}),
 ("skip_invalid", lambda: len(m.clean_rows(rows))==2),
 ("order", lambda: [r["name"] for r in m.clean_rows(rows)]==["A","C"]),
 ("reject_nonfinite", lambda: m.clean_rows([{"name":"X","amount":"NaN"}])==[]),
 ("input_unchanged", lambda: unchanged()),
 ("empty_rows", lambda: m.clean_rows([])==[]),
]
def unchanged():
    original=[{"name":" A ","amount":"1.25","extra":1}]
    before=[dict(row) for row in original]
    m.clean_rows(original)
    return original==before
results=[]
for name, check in cases:
    try:
        passed=bool(check())
        results.append({"name":name,"passed":passed,"checks":1,"failures":[] if passed else ["acceptance mismatch"]})
    except Exception as error:
        results.append({"name":name,"passed":False,"checks":1,"failures":[type(error).__name__]})
print(json.dumps({"groups":results}))
sys.exit(0 if all(group["passed"] for group in results) else 1)
