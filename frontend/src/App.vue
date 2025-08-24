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
      <GpuPanel />
      <PowerPanel />
      <PeripheralsPanel />
      <div class="card" v-if="sensorMod">
        <h3>Sensors Summary</h3>
        <div class="kv"><span>CPU Package Temp (°C)</span><span>{{ fmt1(sensorMod.cpu?.package_temp_c) }}</span></div>
        <div class="kv"><span>CPU Core Temps (°C)</span><span>{{ fmtArr1(sensorMod.cpu?.core_temps_c) }}</span></div>
        <div class="kv"><span>CPU Package Power (W)</span><span>{{ fmt1(sensorMod.cpu?.package_power_w) }}</span></div>
        <div class="kv"><span>System Total Power (W)</span><span>{{ fmt1(sensorMod.system_total_power_w) }}</span></div>
        <div class="kv"><span>GPU Package Power (W)</span><span>{{ fmt1(sensorMod.gpu?.package_power_w) }}</span></div>
        <div class="kv"><span>SoC Package Power (W)</span><span>{{ fmt1(sensorMod.soc?.package_power_w) }}</span></div>
        <div class="kv"><span>Mainboard Temp (°C)</span><span>{{ fmt1(sensorMod.board?.mainboard_temp_c) }}</span></div>
        <div class="kv"><span>Chipset Temp (°C)</span><span>{{ fmt1(sensorMod.board?.chipset_temp_c) }}</span></div>
        <div class="kv"><span>Fan RPM</span><span>{{ fmtArrInt(sensorMod.fan_rpm) }}</span></div>
        <div class="kv"><span>Fan Count</span><span>{{ sensorMod.fan_count ?? 0 }}</span></div>
        <div class="kv"><span>Temperatures</span><span>{{ (sensorMod.temperatures?.length || 0) }}</span></div>
        <div class="kv"><span>Fan Details</span><span>{{ (sensorMod.fan_details?.length || 0) }}</span></div>
        <div class="kv"><span>Fan Controls</span><span>{{ (sensorMod.fan_control_details?.length || 0) }}</span></div>
        <div class="kv"><span>Powers (keys)</span><span>{{ sensorMod.powers_w ? Object.keys(sensorMod.powers_w).length : 0 }}</span></div>
        <div class="kv"><span>Voltages (keys)</span><span>{{ sensorMod.voltages_v ? Object.keys(sensorMod.voltages_v).length : 0 }}</span></div>
        <div class="kv"><span>Loads (keys)</span><span>{{ sensorMod.loads_percent ? Object.keys(sensorMod.loads_percent).length : 0 }}</span></div>
        <div class="kv"><span>Clocks (keys)</span><span>{{ sensorMod.clocks_mhz ? Object.keys(sensorMod.clocks_mhz).length : 0 }}</span></div>
        <div class="kv"><span>Currents (keys)</span><span>{{ sensorMod.currents_a ? Object.keys(sensorMod.currents_a).length : 0 }}</span></div>
        <div class="kv"><span>Controls (keys)</span><span>{{ sensorMod.controls_percent ? Object.keys(sensorMod.controls_percent).length : 0 }}</span></div>
        <div class="kv"><span>Flows (keys)</span><span>{{ sensorMod.flows_lpm ? Object.keys(sensorMod.flows_lpm).length : 0 }}</span></div>
        <div class="kv"><span>Levels (keys)</span><span>{{ sensorMod.levels_percent ? Object.keys(sensorMod.levels_percent).length : 0 }}</span></div>
        <div class="kv"><span>Factors (keys)</span><span>{{ sensorMod.factors ? Object.keys(sensorMod.factors).length : 0 }}</span></div>
      </div>
      <div class="card" v-if="sensorMod">
        <h3>Sensors Tables</h3>
        <div class="sub">按类别展开全部 Key/Value</div>
        <div class="tbl-wrap">
          <h4>Temperatures</h4>
          <table class="tbl" v-if="Array.isArray(sensorMod.temperatures) && sensorMod.temperatures.length">
            <thead><tr><th>Key</th><th>°C</th></tr></thead>
            <tbody>
              <tr v-for="(it,i) in sensorMod.temperatures" :key="'temp-'+i">
                <td>{{ it.key }}</td>
                <td class="num">{{ fmt1(it.c) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Fan Details</h4>
          <table class="tbl" v-if="Array.isArray(sensorMod.fan_details) && sensorMod.fan_details.length">
            <thead><tr><th>Key</th><th>RPM</th></tr></thead>
            <tbody>
              <tr v-for="(it,i) in sensorMod.fan_details" :key="'fan-'+i">
                <td>{{ it.key }}</td>
                <td class="num">{{ Math.max(0, Math.round(it.rpm || 0)) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Fan Control Details</h4>
          <table class="tbl" v-if="Array.isArray(sensorMod.fan_control_details) && sensorMod.fan_control_details.length">
            <thead><tr><th>Key</th><th>%</th></tr></thead>
            <tbody>
              <tr v-for="(it,i) in sensorMod.fan_control_details" :key="'fanctl-'+i">
                <td>{{ it.key }}</td>
                <td class="num">{{ fmt1(it.percent) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Powers (W)</h4>
          <table class="tbl" v-if="entries(sensorMod.powers_w).length">
            <thead><tr><th>Key</th><th>W</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.powers_w)" :key="'pow-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Voltages (V)</h4>
          <table class="tbl" v-if="entries(sensorMod.voltages_v).length">
            <thead><tr><th>Key</th><th>V</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.voltages_v)" :key="'volt-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Loads (%)</h4>
          <table class="tbl" v-if="entries(sensorMod.loads_percent).length">
            <thead><tr><th>Key</th><th>%</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.loads_percent)" :key="'load-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Clocks (MHz)</h4>
          <table class="tbl" v-if="entries(sensorMod.clocks_mhz).length">
            <thead><tr><th>Key</th><th>MHz</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.clocks_mhz)" :key="'clk-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Currents (A)</h4>
          <table class="tbl" v-if="entries(sensorMod.currents_a).length">
            <thead><tr><th>Key</th><th>A</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.currents_a)" :key="'cur-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Controls (%)</h4>
          <table class="tbl" v-if="entries(sensorMod.controls_percent).length">
            <thead><tr><th>Key</th><th>%</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.controls_percent)" :key="'ctl-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Flows (L/min)</h4>
          <table class="tbl" v-if="entries(sensorMod.flows_lpm).length">
            <thead><tr><th>Key</th><th>L/min</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.flows_lpm)" :key="'flow-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Levels (%)</h4>
          <table class="tbl" v-if="entries(sensorMod.levels_percent).length">
            <thead><tr><th>Key</th><th>%</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.levels_percent)" :key="'lvl-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>

          <h4>Factors</h4>
          <table class="tbl" v-if="entries(sensorMod.factors).length">
            <thead><tr><th>Key</th><th>Value</th></tr></thead>
            <tbody>
              <tr v-for="([k,v],i) in entries(sensorMod.factors)" :key="'fac-'+i">
                <td>{{ k }}</td>
                <td class="num">{{ fmt1(v) }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else>—</div>
        </div>

        <details class="json-box" :open="showSensorJson">
          <summary @click.prevent="showSensorJson = !showSensorJson">Sensor JSON</summary>
          <pre><code>{{ sensorModJson }}</code></pre>
        </details>
      </div>
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
import PowerPanel from './components/PowerPanel.vue';
import PeripheralsPanel from './components/PeripheralsPanel.vue';
import GpuPanel from './components/GpuPanel.vue';
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
      const result = await service.start?.({ modules: ['cpu', 'mem', 'disk', 'network', 'gpu', 'sensor', 'power', 'peripherals'] });
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

// 传感器全量列表（当设置 SYS_SENSOR_DUMP_ALL=1 时由后端附带），路径：metrics.latest.sensor.dump_all.sensors
const sensors = computed<any[]>(() => {
  const sensor: any = (metrics.latest as any)?.sensor;
  const arr = sensor?.dump_all?.sensors;
  return Array.isArray(arr) ? arr : [];
});
// 便捷访问 sensor 模块主体
const sensorMod = computed<any>(() => (metrics.latest as any)?.sensor ?? null);
// 简易格式化
const fmt1 = (v: any) => (typeof v === 'number' && isFinite(v)) ? v.toFixed(1) : (v == null ? '—' : String(v));
const fmtArr1 = (arr: any) => Array.isArray(arr)
  ? arr.filter((x: any) => typeof x === 'number' && isFinite(x)).map((x: number) => x.toFixed(1)).join(', ')
  : (arr == null ? '—' : '');
const fmtArrInt = (arr: any) => Array.isArray(arr)
  ? arr.filter((x: any) => typeof x === 'number' && isFinite(x)).map((x: number) => Math.max(0, Math.round(x))).join(', ')
  : (arr == null ? '—' : '');
const sensorVal = (s: any) => (typeof s?.value === 'number' && isFinite(s.value)) ? s.value.toFixed(3) : String(s?.value ?? '-');
// 实用：对象转 entries（空对象返回空数组）
const entries = (o: any): [string, any][] => (o && typeof o === 'object') ? Object.entries(o) as any : [];
// 传感器模块 JSON 展示
const showSensorJson = ref<boolean>(false);
const sensorModJson = computed<string>(() => {
  try { return JSON.stringify(sensorMod.value, null, 2); } catch { return '{}'; }
});

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
.tbl-wrap { margin-top: 8px; display: grid; gap: 10px; }
.tbl { width: 100%; border-collapse: collapse; font-size: 12px; }
.tbl th, .tbl td { border-bottom: 1px dashed #eee; padding: 6px 8px; text-align: left; }
.tbl th { color: #666; font-weight: 600; }
.tbl td.num { text-align: right; font-variant-numeric: tabular-nums; }
/* 采集耗时概览 */
.diag-list { display: grid; gap: 6px; margin-top: 8px; }
.diag-row { display: grid; grid-template-columns: 1fr 80px 80px 80px 70px 70px; gap: 8px; font-size: 12px; align-items: center; }
.diag-row.head { color: #666; font-weight: 600; }
.diag-row .name { font-weight: 600; color: #333; }
/* Sensors Summary */
.kv { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; padding: 4px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
</style>
