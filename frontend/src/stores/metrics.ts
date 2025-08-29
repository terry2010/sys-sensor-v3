import { defineStore } from 'pinia';
import { service } from '../api/service';
import type { SnapshotResult } from '../api/dto';

export type MetricPoint = {
  ts: number;
  cpu?: { usage_percent: number };
  memory?: { total_mb: number; used_mb: number };
  gpu?: any;
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
    per_interface_info?: Array<{
      if_id: string;
      name: string;
      type?: string | null;
      status?: string | null;
      mac_address?: string | null;
      mtu?: number | null;
      ip_addresses?: string[];
      gateways?: string[];
      dns_servers?: string[];
      search_domains?: string[];
      rx_errors?: number | null;
      tx_errors?: number | null;
      rx_drops?: number | null;
      tx_drops?: number | null;
      collisions?: number | null;
      link_speed_mbps?: number | null;
    }>;
    per_ethernet_info?: Array<{
      if_id: string;
      name: string;
      link_speed_mbps?: number | null;
      duplex?: 'full' | 'half' | null | string;
      auto_negotiation?: boolean | null;
    }>;
  };
  power?: SnapshotResult['power'];
  peripherals?: SnapshotResult['peripherals'];
};

let started = false;

export const useMetricsStore = defineStore('metrics', {
  state: () => ({
    latest: null as MetricPoint | null,
    history: [] as MetricPoint[],
    maxHistory: 120, // 减少历史记录数量，避免内存过度增长
    lastAt: 0,
    count: 0,
  }),
  actions: {
    start() {
      if (started) return; started = true;
      service.onMetrics((p: MetricPoint) => {
        this.latest = p;
        this.history.push(p);
        // 减少历史记录上限，同时实现更激进的清理策略
        if (this.history.length > this.maxHistory) {
          // 如果超出限制太多，一次性清理更多数据，避免频繁操作数组
          if (this.history.length > this.maxHistory * 1.5) {
            this.history = this.history.slice(-this.maxHistory);
          } else {
            this.history.shift();
          }
        }
        this.lastAt = Date.now();
        this.count += 1;
        try { if ((p as any)?.gpu) console.debug('[metrics] gpu:', (p as any).gpu); } catch {}
        const w: any = typeof window !== 'undefined' ? window : {};
        if (!w.__METRICS_READY) w.__METRICS_READY = true;
      });
    }
  }
});
