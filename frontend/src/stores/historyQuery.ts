import { defineStore } from 'pinia';

export type HistoryParams = {
  from_ts: number;
  to_ts?: number;
  step_ms?: number;
  modules?: string[];
  agg?: 'raw' | '10s' | '1m';
};

export type HistoryItem = {
  ts: number;
  cpu?: { usage_percent: number } | null;
  memory?: { total: number; used: number } | null;
};

export const useHistoryQueryStore = defineStore('historyQuery', {
  state: () => ({
    params: null as HistoryParams | null,
    items: [] as HistoryItem[],
  }),
  actions: {
    setResult(params: HistoryParams, items: HistoryItem[]) {
      this.params = params;
      this.items = items ?? [];
    }
  }
});
