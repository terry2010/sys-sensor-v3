import { describe, it, expect, beforeEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useToastStore } from '../toast';

describe('toast store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers();
  });

  it('push and remove', () => {
    const s = useToastStore();
    s.push('hello', 'info', 1000);
    expect(s.items.length).toBe(1);
    const id = s.items[0].id;
    s.remove(id);
    expect(s.items.length).toBe(0);
  });

  it('auto remove by ttl', () => {
    const s = useToastStore();
    s.push('auto', 'warn', 1000);
    expect(s.items.length).toBe(1);
    vi.advanceTimersByTime(999);
    expect(s.items.length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(s.items.length).toBe(0);
  });
});
