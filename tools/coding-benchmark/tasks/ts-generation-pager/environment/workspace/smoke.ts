import { GenerationPager } from "./src/pager.ts";

const pages = new Map<string | null, { items: number[]; nextCursor: string | null }>([
  [null, { items: [1, 2], nextCursor: "p2" }],
  ["p2", { items: [3], nextCursor: null }],
]);
const pager = new GenerationPager<number>(async cursor => pages.get(cursor)!);
await pager.loadNext();
await pager.loadNext();
if (pager.snapshot().items.join(",") !== "1,2,3" || pager.snapshot().hasMore) {
  throw new Error("visible paging example failed");
}
console.log("visible smoke checks passed");
