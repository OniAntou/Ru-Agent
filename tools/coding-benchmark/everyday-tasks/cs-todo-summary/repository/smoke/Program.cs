using Everyday;
var b=new TodoBoard(); b.Add(new("Ana",false)); b.Add(new("Ana",true));
if(b.CountPendingByOwner()["Ana"]!=1 || b.CountDone()!=1) throw new Exception("pending summary failed");
Console.WriteLine("smoke passed");
