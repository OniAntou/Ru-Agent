function abortError() {
  const error = new Error("The caller stopped waiting for the shared request");
  error.name = "AbortError";
  return error;
}

function waitForCaller(promise, signal) {
  if (!signal) return promise;
  if (signal.aborted) return Promise.reject(abortError());
  return new Promise((resolve, reject) => {
    let settled = false;
    const cleanup = () => signal.removeEventListener("abort", onAbort);
    const onAbort = () => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(abortError());
    };
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      value => { if (!settled) { settled = true; cleanup(); resolve(value); } },
      error => { if (!settled) { settled = true; cleanup(); reject(error); } },
    );
  });
}

export function createCoalescer(loader) {
  const pending = new Map();
  return {
    run(key, signal) {
      if (signal?.aborted) return Promise.reject(abortError());
      let promise = pending.get(key);
      if (!promise) {
        promise = Promise.resolve().then(() => loader(key));
        pending.set(key, promise);
        promise.finally(() => {
          if (pending.get(key) === promise) pending.delete(key);
        }).catch(() => {});
      }
      return waitForCaller(promise, signal);
    },
    clear(key) { pending.delete(key); },
    size() { return pending.size; },
  };
}
