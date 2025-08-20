import { defineStore } from 'pinia';
import { service } from '../api/service';

export type MetricPoint = { ts: number; cpu?: { usage_percent: number }; memory?: { total: number; used: number } };

export const useMetricsStore = defineStore('metrics', {
  state: () => ({ latest: null as MetricPoint | null, history: [] as MetricPoint[] }),
  actions: {
    start() {
      service.onMetrics((p) => {
        this.latest = p; this.history.push(p);
        if (this.history.length > 300) this.history.shift();
      });
    }
  }
});
