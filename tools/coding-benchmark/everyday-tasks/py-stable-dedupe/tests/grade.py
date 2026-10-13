import importlib,json,os,sys,tempfile
from pathlib import Path
sys.path.insert(0,os.environ['BENCHMARK_CANDIDATE_ROOT'])
module=importlib.import_module('src.records')
def raises(kind,action):
    try: action()
    except kind: return
    raise AssertionError('expected '+kind.__name__)
cases=[('stable_first', 'rows=[{"id":2,"v":"first"},{"id":1},{"id":2,"v":"second"}]; assert module.dedupe_records(rows,"id")==[rows[0],rows[1]]'), ('missing_and_none', 'rows=[{"v":1},{"id":None},{"v":2},{"id":None}]; assert module.dedupe_records(rows,"id")==rows[:3]'), ('falsy_ids', 'rows=[{"id":0},{"id":""},{"id":0},{"id":""},{"id":"0"}]; assert module.dedupe_records(rows,"id")==[rows[0],rows[1],rows[4]]'), ('iterable_and_empty', 'assert module.dedupe_records(iter([{"id":"a"},{"id":"a"}]),"id")==[{"id":"a"}]; assert module.dedupe_records([],"id")==[]'), ('shallow_copies', 'nested=[]; rows=[{"id":1,"nested":nested}]; result=module.dedupe_records(rows,"id"); assert result is not rows and result[0] is not rows[0] and result[0]["nested"] is nested; result[0]["id"]=9; assert rows[0]["id"]==1'), ('existing_ids', 'assert module.record_ids([{"id":0},{"x":1},{"id":"a"}],"id")==[0,"a"]')]
groups=[]
for name,code in cases:
    try:
        exec(code,dict(globals()))
        groups.append(dict(name=name,passed=True,checks=1,failures=[]))
    except Exception as error:
        groups.append(dict(name=name,passed=False,checks=1,failures=[type(error).__name__+': '+str(error)]))
print(json.dumps(dict(groups=groups)))
sys.exit(0 if all(g['passed'] for g in groups) else 1)
