using Everyday;
using System.Text.Json;
var groups = new List<object>();
bool failed = false;
void Check(string name, Action test) {
 try { test(); groups.Add(new { name, passed=true, checks=1, failures=Array.Empty<string>() }); }
 catch(Exception error) { failed=true; groups.Add(new { name, passed=false, checks=1, failures=new[]{error.GetType().Name+": "+error.Message} }); }
}
void Expect(bool value) { if(!value) throw new Exception("behavior mismatch"); }
void Throws<T>(Action test) where T:Exception { try { test(); } catch(T) { return; } throw new Exception("expected "+typeof(T).Name); }
Check("pending_counts", () => { var b=new TodoBoard(); b.Add(new("Ana",false)); b.Add(new("Ana",false)); b.Add(new("Ana",true)); Expect(b.CountPendingByOwner()["Ana"]==2); });
Check("normalized_owners", () => { var b=new TodoBoard(); b.Add(new(" Ana ",false)); b.Add(new(null,false)); b.Add(new(" ",false)); b.Add(new("",false)); var r=b.CountPendingByOwner(); Expect(r["Ana"]==1 && r["(unassigned)"]==3); });
Check("ordinal_order", () => { var b=new TodoBoard(); foreach(var s in new[]{"b","a","A"}) b.Add(new(s,false)); Expect(b.CountPendingByOwner().Keys.SequenceEqual(new[]{"A","a","b"})); });
Check("empty_and_done_only", () => { var b=new TodoBoard(); Expect(b.CountPendingByOwner().Count==0); b.Add(new("x",true)); Expect(b.CountPendingByOwner().Count==0); });
Check("snapshot_independence", () => { var b=new TodoBoard(); b.Add(new("x",false)); var first=b.CountPendingByOwner(); b.Add(new("x",false)); Expect(first["x"]==1 && b.CountPendingByOwner()["x"]==2); if(first is IDictionary<string,int> mutable && !mutable.IsReadOnly) mutable["x"]=99; Expect(b.CountPendingByOwner()["x"]==2); });
Check("existing_operations", () => { var b=new TodoBoard(); b.Add(new("x",true)); b.Add(new("y",false)); Expect(b.CountDone()==1); });
Console.WriteLine(JsonSerializer.Serialize(new {groups}));
return failed ? 1 : 0;
