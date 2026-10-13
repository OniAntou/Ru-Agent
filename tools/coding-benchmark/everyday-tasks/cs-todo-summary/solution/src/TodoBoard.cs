namespace Everyday;
public sealed record Todo(string? Owner,bool Done);
public sealed class TodoBoard {
 private readonly List<Todo> items=[];
 public void Add(Todo item) => items.Add(item);
 public int CountDone() => items.Count(x=>x.Done);
 public IReadOnlyDictionary<string,int> CountPendingByOwner() {
 var result=new SortedDictionary<string,int>(StringComparer.Ordinal);
 foreach(var item in items.Where(x=>!x.Done)) {
  var owner=string.IsNullOrWhiteSpace(item.Owner)?"(unassigned)":item.Owner.Trim();
  result[owner]=result.GetValueOrDefault(owner)+1;
 }
 return result;
}
}
