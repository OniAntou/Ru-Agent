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
Check("second_page", () => { var p=Enumerable.Range(0,6).Select(i=>new Product("item"+i)).ToArray(); Expect(Catalog.Search(p,"item",2,2).Select(x=>x.Name).SequenceEqual(new[]{"item2","item3"})); });
Check("filter_before_page", () => { Product[] p=[new("skip"),new("match1"),new("skip2"),new("match2"),new("match3")]; Expect(Catalog.Search(p,"match",1,2).Select(x=>x.Name).SequenceEqual(new[]{"match2","match3"})); });
Check("case_insensitive", () => { Expect(Catalog.Search([new("Alpha"),new("beta")],"ALP",0,5).Single().Name=="Alpha"); });
Check("empty_and_bounds", () => { Expect(Catalog.Search([new("a")],"",0,0).Count==0); Expect(Catalog.Search([new("a")],"",8,5).Count==0); });
Check("invalid_arguments", () => { Throws<ArgumentOutOfRangeException>(()=>Catalog.Search([],"",-1,1)); Throws<ArgumentOutOfRangeException>(()=>Catalog.Search([],"",0,-1)); Throws<ArgumentNullException>(()=>Catalog.Search(null!,"",0,1)); Throws<ArgumentNullException>(()=>Catalog.Search([],null!,0,1)); });
Check("preserve_input", () => { Product[] p=[new("z"),new("a")]; var r=Catalog.Search(p,"",0,1); Expect(p.Select(x=>x.Name).SequenceEqual(new[]{"z","a"})); Expect(ReferenceEquals(r[0],p[0])); });
Console.WriteLine(JsonSerializer.Serialize(new {groups}));
return failed ? 1 : 0;
