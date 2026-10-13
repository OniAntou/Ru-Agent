from src.records import dedupe_records, record_ids
rows=[{"id":1,"name":"first"},{"id":1,"name":"second"},{"id":2}]
assert dedupe_records(rows,"id")==[rows[0],rows[2]]
assert record_ids(rows,"id")==[1,1,2]
print("smoke passed")
