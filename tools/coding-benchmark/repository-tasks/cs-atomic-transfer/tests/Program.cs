using RepositoryLedger;
using System.Text.Json;
var groups = new List<object>();
bool failed = false;
void Check(string name, Action test) { try { test(); groups.Add(new { name, passed=true, checks=1, failures=Array.Empty<string>() }); } catch(Exception error) { failed=true; groups.Add(new { name, passed=false, checks=1, failures=new[]{error.GetType().Name+": "+error.Message} }); } }
void Expect(bool value) { if(!value) throw new Exception("behavior mismatch"); }
Check("ordered_batch", () => { var l=new AtomicLedger(new[]{new KeyValuePair<string,decimal>("a",10),new("b",0),new("c",0)}); Expect(l.ApplyBatch("one",new[]{new Transfer("t1","a","b",6),new Transfer("t2","b","c",5)})); Expect(l.Balance("a")==4 && l.Balance("b")==1 && l.Balance("c")==5); });
Check("atomic_rejection", () => { var l=new AtomicLedger(new[]{new KeyValuePair<string,decimal>("a",10),new("b",0)}); Expect(!l.ApplyBatch("bad",new[]{new Transfer("t1","a","b",5),new Transfer("t2","a","b",6)})); Expect(l.Balance("a")==10 && l.Balance("b")==0); });
Check("idempotent_replay", () => { var l=new AtomicLedger(new[]{new KeyValuePair<string,decimal>("a",10),new("b",0)}); var batch=new[]{new Transfer("t1","a","b",4)}; Expect(l.ApplyBatch("one",batch)); Expect(l.ApplyBatch("one",batch)); Expect(l.Balance("a")==6 && l.Balance("b")==4); });
Check("duplicate_transfer_rejected", () => { var l=new AtomicLedger(new[]{new KeyValuePair<string,decimal>("a",10),new("b",0)}); Expect(!l.ApplyBatch("one",new[]{new Transfer("same","a","b",2),new Transfer("same","a","b",2)})); Expect(l.Balance("a")==10 && l.Balance("b")==0); });
Check("starting_balances", () => { var l=new AtomicLedger(new[]{new KeyValuePair<string,decimal>("a",3)}); Expect(l.Balance("a")==3 && l.Balance("missing")==0); });
Check("unknown_balance", () => { var l=new AtomicLedger(Array.Empty<KeyValuePair<string,decimal>>()); Expect(l.Balance("new-account")==0); });
Console.WriteLine(JsonSerializer.Serialize(new {groups}));
return failed ? 1 : 0;
