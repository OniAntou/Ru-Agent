import { TransactionStore } from "./src/store.ts";

const store = new TransactionStore({ counter: "1" });
const result = store.transact("increment", 0, [{ op: "set", key: "counter", value: "2" }]);
if (result.code !== "ok" || result.version !== 1 || store.snapshot().values.counter !== "2") {
  throw new Error("visible transaction example failed");
}
console.log("visible smoke checks passed");
