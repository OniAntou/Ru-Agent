import runpy

report = runpy.run_path("src/reports/giá báo cáo.py")
rows = [{"currency": " vnd ", "price": 10, "quantity": 2}, {"currency": "VND", "price": 3}]
assert report["summarize"](rows) == {"VND": 23}
assert rows[0]["currency"] == " vnd "
print("smoke passed")
