export function createCoalescer(loader) {
  const pending = new Map();
  return {
    run(key, signal) { return loader(key, signal); },
    clear(key) { pending.delete(key); },
    size() { return pending.size; },
  };
}
