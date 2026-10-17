interface Entry<T> {
  value: T;
  bytes: number;
}

/** FIFO with bounded retained items and O(1) dequeue while consumers pause. */
export class BoundedQueue<T> {
  #items: Array<Entry<T> | undefined> = [];
  #head = 0;
  #bytes = 0;

  constructor(readonly maxItems: number, readonly maxBytes: number) {}

  get length(): number {
    return this.#items.length - this.#head;
  }

  push(value: T, bytes: number): boolean {
    if (this.length >= this.maxItems || bytes > this.maxBytes - this.#bytes) return false;
    this.#items.push({ value, bytes });
    this.#bytes += bytes;
    return true;
  }

  shift(): T | undefined {
    if (this.#head >= this.#items.length) return undefined;
    const entry = this.#items[this.#head];
    this.#items[this.#head++] = undefined;
    if (!entry) return undefined;
    this.#bytes -= entry.bytes;
    if (this.#head === this.#items.length) {
      this.#items = [];
      this.#head = 0;
    } else if (this.#head >= 256 && this.#head * 2 >= this.#items.length) {
      this.#items = this.#items.slice(this.#head);
      this.#head = 0;
    }
    return entry.value;
  }

  clear(): void {
    this.#items = [];
    this.#head = 0;
    this.#bytes = 0;
  }
}
