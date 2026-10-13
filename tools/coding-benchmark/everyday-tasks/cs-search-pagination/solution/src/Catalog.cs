namespace Everyday;
public sealed record Product(string Name);
public static class Catalog {
 public static IReadOnlyList<Product> Search(IReadOnlyList<Product> products, string query, int offset, int limit) {
  ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(query);
  if(offset<0) throw new ArgumentOutOfRangeException(nameof(offset));
  if(limit<0) throw new ArgumentOutOfRangeException(nameof(limit));
  return products.Where(p=>p.Name.Contains(query,StringComparison.OrdinalIgnoreCase)).Skip(offset).Take(limit).ToArray();
 }
}
