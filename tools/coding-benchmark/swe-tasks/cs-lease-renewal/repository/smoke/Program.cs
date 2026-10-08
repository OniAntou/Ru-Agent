using LeaseRegistry;

var registry = new LeaseRegistry.LeaseRegistry();
var acquired = registry.Acquire("resource-a", "worker-a", TimeSpan.FromMinutes(1));
if (!acquired.Succeeded || acquired.Current?.Version != 1)
    return 1;

var renewed = registry.Renew("resource-a", "worker-a", acquired.Current.Version, TimeSpan.FromMinutes(2));
if (!renewed.Succeeded || renewed.Current?.Version != 2)
    return 1;

Console.WriteLine("lease smoke passed");
return 0;
