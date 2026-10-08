using SingleFlight;

var calls = 0;
var cache = new SingleFlightCache<string, int>((_, _) => Task.FromResult(Interlocked.Increment(ref calls)), TimeSpan.FromMinutes(1), 4);
var value = await cache.GetAsync("visible");
if (value != 1 || await cache.GetAsync("visible") != 1 || calls != 1)
    throw new InvalidOperationException("same-key cache smoke check failed");
Console.WriteLine("visible smoke checks passed");
