<template>
  <div class="container">
    <header>
      <h1>SysSensorV3 DebugView</h1>
      <p class="sub">Tauri + Vue（当前来源：{{ rpcSource }}）</p>
      <div class="health">
        <span>events: {{ metrics.count }}</span>
        <span :class="{ stale: isStale }">last: {{ lastDeltaSec }}s</span>
        <span class="badge" :class="rpcSourceClass">RPC: {{ rpcSource }}</span>
        <span class="badge" :class="bridgeStatusClass">Bridge: {{ bridgeStore.status }}</span>
        <span v-if="bridgeStore.rx > 0" class="badge ok">bridge_rx: {{ bridgeStore.rx }}</span>
        <span v-if="bridgeStore.err > 0" class="badge warn">bridge_err: {{ bridgeStore.err }}</span>
        <span v-if="state.phase" class="badge" :class="stateBadgeClass">state: {{ state.phase }} ({{ stateDeltaSec }}s)</span>
      </div>
      <div v-if="session.error" class="error">会话失败：{{ session.error }}</div>
      <div v-else-if="isStale" class="warn">未收到 metrics，请确认后端是否运行，或稍候片刻…</div>
    </header>

    <section class="cards">
      <div class="card" v-if="diagItems.length">
        <h3>采集耗时概览</h3>
        <div class="sub">数据来自 metrics.collectors_diag（滑窗 p50/p95/p99）</div>
        <div class="diag-list">
          <div class="diag-row head">
            <span>collector</span>
            <span>last(ms)</span>
            <span>p95(ms)</span>
            <span>p99(ms)</span>
            <span>timeout</span>
            <span>error</span>
          </div>
          <div class="diag-row" v-for="it in diagItems" :key="it.name">
            <span class="name">{{ it.name }}</span>
            <span>{{ fmtMs(it.last_ms) }}</span>
            <span>{{ fmtMs(it.p95_ms) }}</span>
            <span>{{ fmtMs(it.p99_ms) }}</span>
            <span :class="{ warn: it.timeout > 0 }">{{ it.timeout }}</span>
            <span :class="{ warn: it.error > 0 }">{{ it.error }}</span>
          </div>
        </div>
      </div>
      <CpuPanel />
      <MemoryPanel />
      <NetworkPanel />
      <DiskPanel />
      <div class="card" v-if="Array.isArray(sensors) && sensors.length">
        <h3>Hardware Sensors (LibreHardwareMonitor)</h3>
        <div class="sub">total: {{ sensors.length }}</div>
        <div class="filters">
          <label>
            hw_type
            <select v-model="filterHwType">
              <option value="">All</option>
              <option v-for="t in hwTypes" :key="t" :value="t">{{ t }}</option>
            </select>
          </label>
          <label>
            sensor_type
            <select v-model="filterSensorType">
              <option value="">All</option>
              <option v-for="t in sensorTypes" :key="t" :value="t">{{ t }}</option>
            </select>
          </label>
          <label class="grow">
            keyword
            <input v-model.trim="keyword" placeholder="name contains..." />
          </label>
          <button class="btn" @click="showJson = !showJson">{{ showJson ? 'Hide JSON' : 'View JSON' }}</button>
        </div>
        <details v-if="showJson" open class="json-box">
          <summary>JSON</summary>
          <pre><code>{{ sensorsJson }}</code></pre>
        </details>
        <div class="sensor-list">
          <div class="sensor-row" v-for="(s, i) in sensorsFiltered" :key="i">
            <span class="t">{{ s.hw_type }}</span>
            <span class="n">{{ s.hw_name }}</span>
            <span class="t">{{ s.sensor_type }}</span>
            <span class="n">{{ s.sensor_name }}</span>
            <span class="v">{{ sensorVal(s) }}</span>
          </div>
        </div>
      </div>
      <SnapshotPanel />
      <HistoryChart />
      <ControlPanel />
      <HistoryQuery />
      <DebugEvents />
    </section>
    <ToastList />
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue';
import SnapshotPanel from './components/SnapshotPanel.vue';
import CpuPanel from './components/CpuPanel.vue';
import MemoryPanel from './components/MemoryPanel.vue';
import NetworkPanel from './components/NetworkPanel.vue';
import DiskPanel from './components/DiskPanel.vue';
import HistoryChart from './components/HistoryChart.vue';
import ControlPanel from './components/ControlPanel.vue';
import HistoryQuery from './components/HistoryQuery.vue';
import DebugEvents from './components/DebugEvents.vue';
import ToastList from './components/ToastList.vue';
import { useSessionStore } from './stores/session';
import { useMetricsStore } from './stores/metrics';
import { useEventsStore } from './stores/events';
import { useBridgeStore } from './stores/bridge';
import { useToastStore } from './stores/toast';
import { service } from './api/service';
import { ensureEventBridge } from './api/rpc.tauri';

const session = useSessionStore();
const metrics = useMetricsStore();
const events = useEventsStore();
const toast = useToastStore();
const bridgeStore = useBridgeStore();
const state = ref<{ phase: string | null; ts: number | null }>({ phase: null, ts: null });

onMounted(async () => {
  // 统一初始化顺序：先探测并尝试启动事件桥 -> 再进行会话与 metrics 订阅
  const w: any = typeof window !== 'undefined' ? window : {};
  try {
    // @ts-ignore: 某些环境下未安装类型声明，动态导入用于运行时探测
    await import('@tauri-apps/api/core');
    w.__IS_TAURI__ = true;
    // 先设置订阅开关（通过 bridge_subscribe），确保事件桥首次握手后的初始订阅为 enable=true
    try { void service.subscribeMetrics(true); } catch {}
    // 再非阻塞启动事件桥（不要等待，避免后端异常导致 UI 等待）
    void ensureEventBridge();
    // 在事件桥初始化后短时间内提升刷新频率（突发 200ms，持续 5s）
    try { void service.burstSubscribe?.({ interval_ms: 200, ttl_ms: 5000 } as any); } catch {}
    // 调试监听：观察桥接是否有事件/错误到达
    try {
      const { listen } = await import('@tauri-apps/api/event');
      void listen('bridge_handshake', (_e: any) => {
        events.push({ ts: Date.now(), type: 'info', payload: { evt: 'bridge_handshake' } });
        toast.push('事件桥已连接', 'success');
      });
      void listen('bridge_disconnected', (_e: any) => {
        events.push({ ts: Date.now(), type: 'warn', payload: { evt: 'bridge_disconnected' } });
        toast.push('事件桥已断开，将重试连接…', 'warn');
      });
      void listen('bridge_rx', (_e: any) => { bridgeStore.rx++; events.push({ ts: Date.now(), type: 'bridge_rx' }); });
      void listen('bridge_error', (_e: any) => { bridgeStore.err++; events.push({ ts: Date.now(), type: 'bridge_error' }); toast.push('事件桥错误', 'error'); });
      // 直接监听 metrics，写入 store（用于绕过 service.onMetrics 的链路验证）
      void listen('metrics', (e: any) => {
        const p = e?.payload as any;
        if (!p) return;
        metrics.latest = p;
        metrics.history.push(p);
        if (metrics.history.length > 300) metrics.history.shift();
        metrics.lastAt = Date.now();
        metrics.count += 1;
        const w: any = typeof window !== 'undefined' ? window : {};
        if (!w.__METRICS_READY) {
          w.__METRICS_READY = true;
          try { toast.push('事件桥已连接', 'success'); } catch {}
        }
        events.push({ ts: Date.now(), type: 'metrics', payload: { cpu: p?.cpu?.usage_percent, mem_used: p?.memory?.used_mb, ts: p?.ts } });
      });
      // 监听 state 事件，展示生命周期状态角标
      void listen('state', (e: any) => {
        const p = e?.payload as any;
        if (!p) return;
        state.value.phase = p.phase ?? null;
        state.value.ts = typeof p.ts === 'number' ? p.ts : Date.now();
        events.push({ ts: Date.now(), type: 'state', payload: p });
      });
    } catch { /* ignore */ }
    // 兜底：监听由 rpcBridge.ts 派发的自定义断线/错误事件
    try {
      const onBridgeStatus = (e: any) => {
        const s = e?.detail?.status as string | undefined;
        if (!s) return;
        if (s === 'disconnected') {
          events.push({ ts: Date.now(), type: 'warn', payload: { evt: 'bridge_disconnected' } });
          toast.push('事件桥已断开，将重试连接…', 'warn');
        } else if (s === 'error') {
          events.push({ ts: Date.now(), type: 'bridge_error' });
          toast.push('事件桥错误', 'error');
        } else if (s === 'connected') {
          events.push({ ts: Date.now(), type: 'info', payload: { evt: 'bridge_connected' } });
        }
      };
      window.addEventListener('bridge_status', onBridgeStatus as any);
      onUnmounted(() => window.removeEventListener('bridge_status', onBridgeStatus as any));
    } catch { /* ignore */ }
  } catch {
    w.__IS_TAURI__ = false;
  }
  // 非阻塞：优先启动 metrics 监听；session.init() 不阻塞主流程
  metrics.start();
  void session.init();
  // 初始化桥接状态 store（监听事件）
  try { bridgeStore.init(); } catch {}
  // 自动启动默认采集模块，确保有数据可推送（移除 2s 延迟，立即启动）
  (async () => {
    try {
      console.log('[App] 尝试自动启动采集模块...');
      const result = await service.start?.({ modules: ['cpu', 'mem', 'disk', 'network'] });
      console.log('[App] 自动启动采集模块成功:', result);
    } catch (e) {
      console.error('[App] 自动启动采集模块失败:', e);
    }
  })();
});

// 健康状态（每秒刷新）
const tick = ref(0);
let t: any = null;
onMounted(() => { t = setInterval(() => tick.value++, 1000); });
onUnmounted(() => { if (t) clearInterval(t); });
const lastDeltaSec = computed(() => metrics.lastAt ? Math.floor((Date.now() - metrics.lastAt) / 1000) : -1);
const isStale = computed(() => lastDeltaSec.value < 0 || lastDeltaSec.value > 5);

// state 角标新鲜度（>10s 视为过期，黄色提示）
const stateBadgeClass = computed(() => {
  const ts = state.value.ts ?? 0;
  if (!ts) return 'warn';
  const age = Date.now() - ts;
  return age <= 10_000 ? 'ok' : 'warn';
});
// state 相对时间（秒）
const stateDeltaSec = computed(() => {
  void tick.value; // 依赖时钟
  const ts = state.value.ts ?? 0;
  if (!ts) return -1;
  return Math.floor((Date.now() - ts) / 1000);
});

// Bridge 状态颜色
const bridgeStatusClass = computed(() => {
  const s = bridgeStore.status;
  return s === 'connected' ? 'ok' : 'warn';
});

// RPC 来源徽标（mock / tauri）
const rpcSource = computed(() => {
  // 依赖 tick 以周期性重评估环境
  void tick.value;
  const w: any = typeof window !== 'undefined' ? window : {};
  return w.__IS_TAURI__ ? 'tauri' : 'mock';
});
const rpcSourceClass = computed(() => rpcSource.value === 'tauri' ? 'ok' : 'warn');

// LHM 传感器列表（来自 metrics.latest.cpu.lhm_sensors）
const sensors = computed<any[]>(() => {
  const cpu: any = metrics.latest?.cpu;
  const arr = cpu?.lhm_sensors;
  return Array.isArray(arr) ? arr : [];
});
const sensorVal = (s: any) => (typeof s?.value === 'number' && isFinite(s.value)) ? s.value.toFixed(3) : String(s?.value ?? '-');

// 过滤与下拉选项
const filterHwType = ref<string>('');
const filterSensorType = ref<string>('');
const keyword = ref<string>('');
const showJson = ref<boolean>(false);
const hwTypes = computed<string[]>(() => Array.from(new Set(sensors.value.map(s => String(s.hw_type || '')))).filter(Boolean).sort());
const sensorTypes = computed<string[]>(() => Array.from(new Set(sensors.value.map(s => String(s.sensor_type || '')))).filter(Boolean).sort());
const sensorsFiltered = computed<any[]>(() => {
  let arr = sensors.value;
  if (filterHwType.value) arr = arr.filter(s => String(s.hw_type) === filterHwType.value);
  if (filterSensorType.value) arr = arr.filter(s => String(s.sensor_type) === filterSensorType.value);
  const kw = keyword.value?.toLowerCase().trim();
  if (kw) {
    arr = arr.filter(s => String(s.sensor_name || '').toLowerCase().includes(kw) || String(s.hw_name || '').toLowerCase().includes(kw));
  }
  return arr;
});
const sensorsJson = computed<string>(() => {
  try { return JSON.stringify(sensorsFiltered.value, null, 2); } catch { return '[]'; }
});

// 采集器耗时诊断（来自 metrics.latest.collectors_diag）
const diagItems = computed<any[]>(() => {
  const diag = (metrics.latest as any)?.collectors_diag;
  if (!diag || typeof diag !== 'object') return [];
  const arr: any[] = [];
  for (const k of Object.keys(diag)) {
    const v = (diag as any)[k] || {};
    arr.push({ name: k, last_ms: v.last_ms ?? null, p95_ms: v.p95_ms ?? null, p99_ms: v.p99_ms ?? null, timeout: v.timeout ?? 0, error: v.error ?? 0 });
  }
  // 固定顺序：先 disk，再 cpu/memory/network，其它按字母
  const order: Record<string, number> = { disk: 0, cpu: 1, memory: 2, network: 3 } as any;
  arr.sort((a, b) => (order[a.name] ?? 100) - (order[b.name] ?? 100) || a.name.localeCompare(b.name));
  return arr;
});
const fmtMs = (v: any) => (typeof v === 'number' && isFinite(v) ? Math.round(v) : '-');

// 探测逻辑已合并进上面的 onMounted 初始化
</script>

<style scoped>
.container {
  padding: 16px;
  font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, 'Microsoft YaHei', sans-serif;
}
header {
  margin-bottom: 16px;
}
.sub { color: #666; font-size: 12px; }
.health { display: flex; gap: 10px; font-size: 12px; margin-top: 6px; }
.health .stale { color: #c00; font-weight: 600; }
.badge { padding: 2px 6px; border-radius: 10px; border: 1px solid #ddd; }
.badge.ok { background: #e6ffed; color: #046c4e; border-color: #b7ebc6; }
.badge.warn { background: #fff7e6; color: #ad4e00; border-color: #ffd591; }
.error { margin-top: 6px; color: #b00020; font-size: 12px; }
.warn { margin-top: 6px; color: #ad4e00; font-size: 12px; }
.cards { display: grid; gap: 16px; grid-template-columns: 1fr; }
@media (min-width: 900px) {
  .cards { grid-template-columns: 1fr 1fr; }
}
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.sensor-list { max-height: 300px; overflow: auto; border-top: 1px dashed #eee; margin-top: 8px; }
.sensor-row { display: grid; grid-template-columns: 90px 1fr 110px 1fr 120px; gap: 8px; padding: 6px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.sensor-row .t { color: #666; }
.sensor-row .n { color: #333; }
.sensor-row .v { font-weight: 600; text-align: right; }
.filters { display: flex; gap: 8px; align-items: end; margin: 8px 0; flex-wrap: wrap; }
.filters label { display: flex; flex-direction: column; font-size: 12px; color: #555; }
.filters .grow { flex: 1; }
.filters input, .filters select { padding: 4px 6px; font-size: 12px; }
.btn { padding: 6px 10px; border: 1px solid #ddd; border-radius: 6px; background: #fafafa; cursor: pointer; }
.btn:hover { background: #f0f0f0; }
.json-box { margin: 8px 0; }
.json-box pre { max-height: 300px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
/* 采集耗时概览 */
.diag-list { display: grid; gap: 6px; margin-top: 8px; }
.diag-row { display: grid; grid-template-columns: 1fr 80px 80px 80px 70px 70px; gap: 8px; font-size: 12px; align-items: center; }
.diag-row.head { color: #666; font-weight: 600; }
.diag-row .name { font-weight: 600; color: #333; }
</style>
