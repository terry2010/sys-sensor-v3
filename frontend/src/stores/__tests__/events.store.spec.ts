import { describe, it, expect, beforeEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useEventsStore } from '../events';

describe('events store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('push and clear', () => {
    const s = useEventsStore();
    expect(s.items.length).toBe(0);
    s.push({ ts: Date.now(), type: 'info', payload: { a: 1 } });
    s.push({ ts: Date.now(), type: 'metrics', payload: { cpu: 1 } });
    expect(s.items.length).toBe(2);
    expect(s.items[0].type).toBe('info');
    expect(s.items[1].type).toBe('metrics');
    s.clear();
    expect(s.items.length).toBe(0);
  });

  it('cap at max size', () => {
    const s = useEventsStore();
    s.max = 3;
    for (let i = 0; i < 10; i++) s.push({ ts: i, type: 'info', payload: i });
    expect(s.items.length).toBe(3);
    // ring buffer by shift, first should be last three inserted start
    expect(s.items[0].ts).toBe(7);
    expect(s.items[2].ts).toBe(9);
  });
});
