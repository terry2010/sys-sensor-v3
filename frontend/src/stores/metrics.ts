import { defineStore } from 'pinia';
import { service } from '../api/service';
import type { SnapshotResult } from '../api/dto';

export type MetricPoint = {
  ts: number;
  cpu?: { usage_percent: number };
  memory?: { total_mb: number; used_mb: number };
  disk?: SnapshotResult['disk'];
  network?: {
    io_totals: {
      rx_bytes_per_sec: number;
      tx_bytes_per_sec: number;
      rx_packets_per_sec: number | null;
      tx_packets_per_sec: number | null;
      rx_errors_per_sec: number | null;
      tx_errors_per_sec: number | null;
      rx_drops_per_sec: number | null;
      tx_drops_per_sec: number | null;
    };
    per_interface_io: Array<{
      if_id: string;
      name: string;
      rx_bytes_per_sec: number;
      tx_bytes_per_sec: number;
      rx_packets_per_sec: number | null;
      tx_packets_per_sec: number | null;
      rx_errors_per_sec: number | null;
      tx_errors_per_sec: number | null;
      rx_drops_per_sec: number | null;
      tx_drops_per_sec: number | null;
      utilization_percent: number | null;
    }>;
  };
};

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
