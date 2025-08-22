import { defineStore } from 'pinia';
import { service } from '../api/service';
import type { SnapshotResult } from '../api/dto';

export type MetricPoint = { ts: number; cpu?: { usage_percent: number }; memory?: { total_mb: number; used_mb: number }; disk?: SnapshotResult['disk'] };

let started = false;

export const useMetricsStore = defineStore('metrics', {
  state: () => ({
    latest: null as MetricPoint | null,
    history: [] as MetricPoint[],
    lastAt: 0,
    count: 0,
  }),
  actions: {
    start() {
      if (started) return; started = true;
      service.onMetrics((p: MetricPoint) => {
        this.latest = p;
        this.history.push(p);
        if (this.history.length > 300) this.history.shift();
        this.lastAt = Date.now();
        this.count += 1;
        const w: any = typeof window !== 'undefined' ? window : {};
        if (!w.__METRICS_READY) w.__METRICS_READY = true;
      });
    }
  }
});
