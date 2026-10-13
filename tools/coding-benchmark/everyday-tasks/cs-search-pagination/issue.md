# Search pagination regression
`src/Catalog.cs` loses results on the second search page. Fix `Catalog.Search`:
filter names with ordinal case-insensitive substring matching, skip `offset`
matching products, then return up to `limit`, retaining input order. Empty query
matches every product. Zero limit or offset beyond results returns an empty list.
Keep current negative-offset/limit and null argument exceptions. Do not mutate
the input collection or product objects. Run `dotnet run --project smoke/Smoke.csproj`.
Do not add unrelated features.
