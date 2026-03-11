export class AsyncQueue<T> implements AsyncIterable<T> {
  private readonly items: T[] = [];
  private readonly waiters: Array<{
    resolve: (value: IteratorResult<T>) => void;
    reject: (reason?: unknown) => void;
  }> = [];
  private closed = false;
  private error: Error | undefined;

  push(item: T): void {
    if (this.closed) {
      return;
    }

    const waiter = this.waiters.shift();
    if (waiter) {
      waiter.resolve({ value: item, done: false });
      return;
    }

    this.items.push(item);
  }

  close(error?: Error): void {
    if (this.closed) {
      return;
    }

    this.closed = true;
    this.error = error;

    while (this.waiters.length > 0) {
      const waiter = this.waiters.shift();
      if (!waiter) {
        break;
      }

      if (this.error) {
        waiter.reject(this.error);
      } else {
        waiter.resolve({ value: undefined as unknown as T, done: true });
      }
    }
  }

  async next(): Promise<IteratorResult<T>> {
    if (this.items.length > 0) {
      const value = this.items.shift()!;
      return { value, done: false };
    }

    if (this.closed) {
      if (this.error) {
        throw this.error;
      }

      return { value: undefined as unknown as T, done: true };
    }

    return new Promise<IteratorResult<T>>((resolve, reject) => {
      this.waiters.push({ resolve, reject });
    });
  }

  [Symbol.asyncIterator](): AsyncIterator<T> {
    return {
      next: () => this.next()
    };
  }
}

export class Mutex {
  private current: Promise<void> = Promise.resolve();

  async run<T>(action: () => T | Promise<T>): Promise<T> {
    const previous = this.current;
    let release: () => void = () => {};
    this.current = new Promise<void>((resolve) => {
      release = resolve;
    });

    await previous;
    try {
      return await action();
    } finally {
      release();
    }
  }
}
