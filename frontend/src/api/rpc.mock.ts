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
      const disk = buildMockDisk();
      const payload = { ts: now, seq: ++this.seq, cpu: { usage_percent: cpu }, memory: { total, used }, disk };
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
    if (mods.includes('disk')) payload.disk = buildMockDisk();
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

function buildMockDisk() {
  const rb = Math.round(10_000_000 + Math.random()*5_000_000);
  const wb = Math.round(6_000_000 + Math.random()*4_000_000);
  const busy = Math.round((rb+wb)/200_000);
  const totals = {
    read_bytes_per_sec: rb,
    write_bytes_per_sec: wb,
    read_iops: Math.round(rb/128/10) + 50,
    write_iops: Math.round(wb/128/10) + 30,
    busy_percent: Math.min(100, busy),
    queue_length: Math.round(busy/10),
    avg_read_latency_ms: Math.round(0.3 + Math.random()*0.4 * 100)/100,
    avg_write_latency_ms: Math.round(0.4 + Math.random()*0.5 * 100)/100,
  };
  const mkPhy = (i: number) => ({
    device_id: `PHYSICALDRIVE${i}`,
    ...totals,
    read_bytes_per_sec: Math.round(totals.read_bytes_per_sec * (0.6 + Math.random()*0.4)),
    write_bytes_per_sec: Math.round(totals.write_bytes_per_sec * (0.6 + Math.random()*0.4)),
  });
  const mkVol = (l: string) => ({
    volume_id: `${l}:\\`,
    ...totals,
    read_bytes_per_sec: Math.round(totals.read_bytes_per_sec * (0.4 + Math.random()*0.6)),
    write_bytes_per_sec: Math.round(totals.write_bytes_per_sec * (0.4 + Math.random()*0.6)),
    free_percent: Math.round(20 + Math.random()*70),
  });
  const per_physical_disk_io = [mkPhy(0), mkPhy(1)];
  const per_volume_io = ['C','D'].map(mkVol);
  const capacity_totals = { total_bytes: 1_000_000_000_000, used_bytes: 450_000_000_000, free_bytes: 550_000_000_000 };
  const per_volume = [
    { id: 'C:', mount_point: 'C:\\', fs_type: 'NTFS', size_total_bytes: 500_000_000_000, size_used_bytes: 300_000_000_000, size_free_bytes: 200_000_000_000, free_percent: 40, read_only: false, is_removable: false },
    { id: 'D:', mount_point: 'D:\\', fs_type: 'NTFS', size_total_bytes: 500_000_000_000, size_used_bytes: 150_000_000_000, size_free_bytes: 350_000_000_000, free_percent: 70, read_only: false, is_removable: false },
  ];
  return {
    read_bytes_per_sec: totals.read_bytes_per_sec,
    write_bytes_per_sec: totals.write_bytes_per_sec,
    queue_length: totals.queue_length ?? 0,
    totals,
    per_physical_disk_io,
    per_volume_io,
    capacity_totals,
    per_volume,
  };
}
