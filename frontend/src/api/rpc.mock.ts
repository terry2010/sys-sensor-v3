// Web Mock 实现：无 Tauri 环境下用于本地开发 UI
import type { QueryHistoryParams, QueryHistoryResult, SnapshotResult, HelloResult, SnapshotParams, GetConfigResult } from './dto';

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

// 可变的 mock 配置，用于模拟 set_config/get_config
type MockConfig = import('./dto').GetConfigResult;
const mockConfig: MockConfig = {
  ok: true,
  base_interval_ms: 1000,
  effective_intervals: { disk: 1000 },
  max_concurrency: 3,
  enabled_modules: ['cpu','memory','disk'],
  sync_exempt_modules: [],
  current_interval_ms: 1000,
  burst_expires_at: 0,
  disk_smart_ttl_ms: 30000,
  disk_nvme_errorlog_ttl_ms: 60000,
  disk_nvme_ident_ttl_ms: 600000,
  disk_smart_native_override: null,
  disk_smart_native_effective: true,
};

export const mockRpc = {
  async hello(): Promise<HelloResult> {
    return {
      server_version: 'mock-1.0.0',
      protocol_version: 1,
      capabilities: ['metrics_stream','history_query'],
      session_id: 'mock-session'
    };
  },
  async get_config(): Promise<GetConfigResult> {
    // 返回当前可变配置
    return { ...mockConfig, effective_intervals: { ...mockConfig.effective_intervals } };
  },
  async set_config(p: import('./dto').SetConfigParams) {
    // 基础/模块级间隔
    if (typeof p.base_interval_ms === 'number' && p.base_interval_ms > 0) {
      mockConfig.base_interval_ms = Math.max(100, p.base_interval_ms|0);
    }
    if (p.module_intervals && typeof p.module_intervals['disk'] === 'number' && p.module_intervals['disk']! > 0) {
      mockConfig.effective_intervals['disk'] = Math.max(100, p.module_intervals['disk']!|0);
    }
    // 并发/模块启用/同步豁免（简单镜像）
    if (typeof p.max_concurrency === 'number') mockConfig.max_concurrency = Math.max(1, Math.min(8, p.max_concurrency|0));
    if (Array.isArray(p.enabled_modules)) mockConfig.enabled_modules = p.enabled_modules.length ? [...p.enabled_modules] : ['cpu','memory','disk'];
    if (Array.isArray(p.sync_exempt_modules)) mockConfig.sync_exempt_modules = [...p.sync_exempt_modules];
    // 磁盘 TTL 与开关（按后端夹紧规则近似）
    if (typeof p.disk_smart_ttl_ms === 'number') mockConfig.disk_smart_ttl_ms = Math.max(5000, Math.min(300000, p.disk_smart_ttl_ms|0));
    if (typeof p.disk_nvme_errorlog_ttl_ms === 'number') mockConfig.disk_nvme_errorlog_ttl_ms = Math.max(10000, Math.min(600000, p.disk_nvme_errorlog_ttl_ms|0));
    if (typeof p.disk_nvme_ident_ttl_ms === 'number') mockConfig.disk_nvme_ident_ttl_ms = Math.max(60000, Math.min(3600000, p.disk_nvme_ident_ttl_ms|0));
    if (typeof p.disk_smart_native_enabled === 'boolean') {
      mockConfig.disk_smart_native_override = p.disk_smart_native_enabled;
      mockConfig.disk_smart_native_effective = p.disk_smart_native_enabled;
    }
    // 计算当前推送间隔（无突发下 = disk 模块间隔 或 base）
    const effDisk = mockConfig.effective_intervals?.['disk'] ?? mockConfig.base_interval_ms;
    mockConfig.current_interval_ms = effDisk || mockConfig.base_interval_ms;
    // 模拟推送频率变化
    try { bus.start(Math.max(100, mockConfig.current_interval_ms)); } catch {}
    return { ok: true, ...mockConfig } as any;
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
