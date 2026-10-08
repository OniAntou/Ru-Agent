export type Page<T> = { items: T[]; nextCursor: string | null };
export type PagerSnapshot<T> = {
  items: readonly T[];
  cursor: string | null;
  hasMore: boolean;
  isLoading: boolean;
  error: unknown | null;
  generation: number;
};

export class GenerationPager<T> {
  private items: T[] = [];
  private cursor: string | null = null;
  private hasMore = true;
  private isLoading = false;
  private error: unknown | null = null;
  private generation = 0;
  private active: Promise<void> | null = null;
  private readonly loadPage: (cursor: string | null) => Promise<Page<T>>;

  constructor(loadPage: (cursor: string | null) => Promise<Page<T>>) {
    this.loadPage = loadPage;
  }

  snapshot(): PagerSnapshot<T> {
    return Object.freeze({ items: Object.freeze([...this.items]), cursor: this.cursor,
      hasMore: this.hasMore, isLoading: this.isLoading, error: this.error, generation: this.generation });
  }

  loadNext(): Promise<void> {
    if (!this.hasMore) return Promise.resolve();
    if (this.active) return this.active;
    const generation = this.generation;
    const cursor = this.cursor;
    this.isLoading = true;
    this.error = null;
    const request = Promise.resolve().then(() => this.loadPage(cursor)).then(page => {
      if (generation !== this.generation) return;
      this.items = [...this.items, ...page.items];
      this.cursor = page.nextCursor;
      this.hasMore = page.nextCursor !== null;
    }).catch(error => {
      if (generation === this.generation) this.error = error;
    }).finally(() => {
      if (generation === this.generation && this.active === request) {
        this.isLoading = false;
        this.active = null;
      }
    });
    this.active = request;
    return request;
  }

  refresh(): Promise<void> {
    this.generation++;
    this.items = [];
    this.cursor = null;
    this.hasMore = true;
    this.isLoading = false;
    this.error = null;
    this.active = null;
    return this.loadNext();
  }
}
