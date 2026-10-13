using Everyday;
Product[] products=[new("book1"),new("book2"),new("book3")];
if(Catalog.Search(products,"book",1,2).Select(p=>p.Name).SequenceEqual(new[]{"book2","book3"}) is false) throw new Exception("second page drops a product");
Console.WriteLine("smoke passed");
