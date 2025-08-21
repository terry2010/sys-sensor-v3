// Web Mock 实现：无 Tauri 环境下用于本地开发 UI
import type { QueryHistoryParams, QueryHistoryResult, SnapshotResult, HelloResult, SnapshotParams } from './dto';

class MockMetricsBus {
  private interval?: number;
  private timer?: number;
  private listeners = new Set<(payload: any) => void>();
  private seq = 0;

  start(intervalMs = 500) {
    this.stop();
    this.interval = intervalMs;
    this.timer = window.setInterval(() => {
      const now = Date.now();
      const cpu = Math.round((Math.random() * 70 + 10) * 10) / 10;
      const total = 16000;
      const used = Math.round(6000 + Math.random() * 4000);
      const payload = { ts: now, seq: ++this.seq, cpu: { usage_percent: cpu }, memory: { total, used } };
      this.listeners.forEach(l => l(payload));
    }, intervalMs);
  }
  stop() { if (this.timer) { clearInterval(this.timer); this.timer = undefined; } }
  on(listener: (payload: any) => void) { this.listeners.add(listener); return () => this.listeners.delete(listener); }
}

const bus = new MockMetricsBus();
bus.start(500);

export const mockRpc = {
  async hello(): Promise<HelloResult> {
    return {
      server_version: 'mock-1.0.0',
      protocol_version: 1,
      capabilities: ['metrics_stream','history_query'],
      session_id: 'mock-session'
    };
  },
  async snapshot(p?: SnapshotParams): Promise<SnapshotResult> {
    const ts = Date.now();
    const cpu = Math.round((Math.random()*80)*10)/10;
    const total = 16000; const used = Math.round(8000 + Math.random()*2000);
    const mods = (p?.modules && p.modules.length) ? p.modules.map(m => m.toLowerCase()) : ['cpu','memory'];
    const payload: any = { ts };
    if (mods.includes('cpu')) payload.cpu = { usage_percent: cpu };
    if (mods.includes('mem') || mods.includes('memory')) payload.memory = { total, used };
    if (mods.includes('disk')) payload.disk = { read_bytes_per_sec: 0, write_bytes_per_sec: 0, queue_length: 0 };
    if (mods.includes('network')) payload.network = { up_bytes_per_sec: 0, down_bytes_per_sec: 0 };
    if (mods.includes('gpu')) {/* omit to mimic null */}
    if (mods.includes('sensor')) {/* omit to mimic null */}
    return payload as SnapshotResult;
  },
  async query_history(p: QueryHistoryParams): Promise<QueryHistoryResult> {
    const step = p.step_ms && p.step_ms > 0 ? p.step_ms : 500;
    const items: QueryHistoryResult['items'] = [];
    for (let t = p.from_ts; t <= p.to_ts; t += step) {
      const usage = Math.max(0, Math.min(100, Math.sin(t/2000) * 20 + 40 + (Math.random()*4-2)));
      items.push({ ts: t, cpu: { usage_percent: Math.round(usage*10)/10 }, memory: { total: 16000, used: 7000 + Math.round(Math.random()*1000) } });
    }
    if (items.length === 0) {
      const now = Date.now();
      items.push({ ts: now, cpu: { usage_percent: 0 }, memory: { total: 16000, used: 8000 } });
    }
    return { ok: true, items };
  },
  onMetrics(listener: (payload: any) => void) {
    return bus.on(listener);
  }
};
