import { defineStore } from 'pinia';

export type ToastItem = { id: number; type: 'info' | 'warn' | 'error' | 'success'; text: string; ts: number; ttl: number };

let seq = 1;

export const useToastStore = defineStore('toast', {
  state: () => ({ items: [] as ToastItem[] }),
  actions: {
    push(text: string, type: ToastItem['type'] = 'info', ttl = 4000) {
      const it: ToastItem = { id: seq++, type, text, ts: Date.now(), ttl };
      this.items.push(it);
      setTimeout(() => this.remove(it.id), ttl);
    },
    remove(id: number) { this.items = this.items.filter(x => x.id !== id); },
    clear() { this.items = []; }
  }
});
