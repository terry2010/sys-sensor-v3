import { defineStore } from 'pinia';

export type DebugEvent = {
  ts: number;
  type: 'metrics' | 'state' | 'bridge_rx' | 'bridge_error' | 'info' | 'warn' | 'error';
  payload?: any;
};

export const useEventsStore = defineStore('events', {
  state: () => ({
    items: [] as DebugEvent[],
    max: 200,
  }),
  actions: {
    push(ev: DebugEvent) {
      this.items.push(ev);
      if (this.items.length > this.max) this.items.shift();
    },
    clear() { this.items = []; }
  }
});
