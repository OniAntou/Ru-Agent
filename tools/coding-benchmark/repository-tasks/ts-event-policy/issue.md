Finish the typed event policy across the model, policy and facade modules.
Normalize event names by trimming and lowercasing, allow only known event names,
and default unknown or missing policy entries to deny. Return a fresh filtered
array so callers cannot mutate the supplied event list. Keep the cross-file
types strict and make the project pass its configured TypeScript type check.
