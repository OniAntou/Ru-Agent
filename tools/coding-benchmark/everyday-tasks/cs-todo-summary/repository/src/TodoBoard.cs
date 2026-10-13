namespace Everyday;
public sealed record Todo(string? Owner,bool Done);
public sealed class TodoBoard {
 private readonly List<Todo> items=[];
 public void Add(Todo item) => items.Add(item);
 public int CountDone() => items.Count(x=>x.Done);
 public IReadOnlyDictionary<string,int> CountPendingByOwner() => throw new NotImplementedException();
}
