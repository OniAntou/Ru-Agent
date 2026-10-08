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
  private readonly loadPage: (cursor: string | null) => Promise<Page<T>>;

  constructor(loadPage: (cursor: string | null) => Promise<Page<T>>) { this.loadPage = loadPage; }

  snapshot(): PagerSnapshot<T> {
    return { items: [...this.items], cursor: this.cursor, hasMore: this.hasMore,
      isLoading: this.isLoading, error: this.error, generation: this.generation };
  }

  async loadNext(): Promise<void> {
    if (!this.hasMore) return;
    this.isLoading = true;
    const page = await this.loadPage(this.cursor);
    this.items = [...this.items, ...page.items];
    this.cursor = page.nextCursor;
    this.hasMore = page.nextCursor !== null;
    this.isLoading = false;
  }

  async refresh(): Promise<void> {
    this.items = [];
    this.cursor = null;
    this.hasMore = true;
    this.error = null;
    await this.loadNext();
  }
}
